#ifndef DOTNET_DIAGNOSTICS_WORKER_MONITOR_H
#define DOTNET_DIAGNOSTICS_WORKER_MONITOR_H

#include <ctype.h>
#include <limits.h>
#include <poll.h>
#include <sys/time.h>
#include <time.h>

#ifndef SYS_pidfd_send_signal
#define SYS_pidfd_send_signal 424
#endif

#define MONITOR_GAP_NS 10000000LL
#define MONITOR_STDIN_BYTES 4
#define MONITOR_STDERR_RETAIN 256
#define MONITOR_STAT_BUFFER 1024

struct monitor_stat {
    long long utime;
    long long stime;
    long long start_time;
    long long rss_pages;
};

struct monitor_limits {
    long long wall_ns;
    long long cpu_ns;
    long long resident_bytes;
};

struct monitor_result {
    const char *outcome;
    int exit_code;
    int signal_number;
    long long peak_rss;
    long long max_gap_ns;
    long long gap_last_valid_ns;
    long long gap_now_ns;
    long long gap_ns;
    long long wall_ns;
    long long cpu_ns;
    long long samples;
    int locked;
    unsigned char stderr_bytes[MONITOR_STDERR_RETAIN];
    size_t stderr_count;
    long long thread_cpu_delta_ns;
    long long involuntary_context_switch_delta;
};

static long long monitor_timespec_ns(struct timespec value)
{
    return value.tv_sec * 1000000000LL + value.tv_nsec;
}

static long long monitor_now_ns(void)
{
    struct timespec value;
    if (clock_gettime(CLOCK_MONOTONIC, &value) != 0) _exit(125);
    return monitor_timespec_ns(value);
}

static long long monitor_thread_cpu_ns(void)
{
    struct timespec value;
    if (clock_gettime(CLOCK_THREAD_CPUTIME_ID, &value) != 0) return 0;
    return monitor_timespec_ns(value);
}

static int monitor_parse_long_long(const char *start, const char *end, long long *value)
{
    if (start >= end) return 0;
    int negative = *start == '-';
    if (negative) start++;
    if (start >= end) return 0;
    long long parsed = 0;
    while (start < end) {
        if (*start < '0' || *start > '9' || parsed > (LLONG_MAX - 9) / 10) return 0;
        parsed = parsed * 10 + (*start - '0');
        start++;
    }
    *value = negative ? -parsed : parsed;
    return 1;
}

static int monitor_parse_stat_bytes(const char *buffer, size_t length, struct monitor_stat *stat)
{
    const char *end = buffer + length;
    const char *close = NULL;
    for (const char *cursor = buffer; cursor < end; cursor++)
        if (*cursor == ')') close = cursor;
    if (close == NULL) return 0;
    const char *cursor = close + 1;
    int field = 2;
    memset(stat, 0, sizeof(*stat));
    while (field < 24) {
        if (cursor >= end || *cursor != ' ') return 0;
        cursor++;
        field++;
        const char *token = cursor;
        while (cursor < end && *cursor != ' ' && *cursor != '\n') cursor++;
        if (token == cursor) return 0;
        long long value = 0;
        if (field == 14 || field == 15 || field == 22 || field == 24) {
            if (!monitor_parse_long_long(token, cursor, &value)) return 0;
            if (field == 14) stat->utime = value;
            if (field == 15) stat->stime = value;
            if (field == 22) stat->start_time = value;
            if (field == 24) stat->rss_pages = value;
        }
    }
    return stat->utime >= 0 && stat->stime >= 0 && stat->start_time > 0 && stat->rss_pages >= 0;
}

static int monitor_read_stat(int stat_fd, struct monitor_stat *stat)
{
    char buffer[MONITOR_STAT_BUFFER];
    ssize_t count = pread(stat_fd, buffer, sizeof(buffer) - 1, 0);
    if (count <= 0 || count >= (ssize_t)sizeof(buffer)) return 0;
    buffer[count] = '\0';
    return monitor_parse_stat_bytes(buffer, (size_t)count, stat);
}

static int monitor_full_write(int fd, const char *buffer, size_t length)
{
    while (length > 0) {
        ssize_t written = write(fd, buffer, length);
        if (written < 0) {
            if (errno == EINTR) continue;
            return 0;
        }
        if (written == 0) return 0;
        buffer += written;
        length -= (size_t)written;
    }
    return 1;
}

static int monitor_report_write(const char *buffer, size_t length)
{
    int flags = fcntl(STDERR_FILENO, F_GETFL, 0);
    if (flags < 0 || fcntl(STDERR_FILENO, F_SETFL, flags | O_NONBLOCK) != 0) return 0;
    long long deadline = monitor_now_ns() + 100000000LL;
    while (length > 0) {
        ssize_t written = write(STDERR_FILENO, buffer, length);
        if (written > 0) {
            buffer += written;
            length -= (size_t)written;
            continue;
        }
        if (written < 0 && errno == EINTR) continue;
        if (written < 0 && errno == EAGAIN) {
            long long remaining = deadline - monitor_now_ns();
            if (remaining <= 0) return 0;
            int timeout = (int)((remaining + 999999LL) / 1000000LL);
            struct pollfd output = { .fd = STDERR_FILENO, .events = POLLOUT };
            int polled;
            do { polled = poll(&output, 1, timeout); } while (polled < 0 && errno == EINTR);
            if (polled <= 0) return 0;
            continue;
        }
        return 0;
    }
    return 1;
}

static int monitor_read_exact_ack(void)
{
    char ack[MONITOR_STDIN_BYTES];
    size_t offset = 0;
    while (offset < sizeof(ack)) {
        ssize_t count = read(STDIN_FILENO, ack + offset, sizeof(ack) - offset);
        if (count < 0) {
            if (errno == EINTR) continue;
            return 0;
        }
        if (count == 0) return 0;
        offset += (size_t)count;
    }
    return memcmp(ack, "ACK\n", sizeof(ack)) == 0;
}

static void monitor_kill_child(pid_t pid, int pidfd)
{
    if (pidfd >= 0 && syscall(SYS_pidfd_send_signal, pidfd, SIGKILL, NULL, 0) == 0) return;
    kill(pid, SIGKILL);
}

static void monitor_reap_child(pid_t pid)
{
    int status;
    while (waitpid(pid, &status, 0) < 0 && errno == EINTR) { }
}

static void monitor_hex(char *destination, size_t destination_size, const unsigned char *source, size_t count)
{
    static const char alphabet[] = "0123456789abcdef";
    size_t offset = 0;
    for (size_t i = 0; i < count && offset + 2 < destination_size; i++) {
        destination[offset++] = alphabet[source[i] >> 4];
        destination[offset++] = alphabet[source[i] & 0xf];
    }
    destination[offset] = '\0';
}

static void monitor_report(const char *nonce, const struct monitor_result *result)
{
    char hex[MONITOR_STDERR_RETAIN * 2 + 1];
    monitor_hex(hex, sizeof(hex), result->stderr_bytes, result->stderr_count);
    const char *stderr_hex = result->stderr_count == 0 ? "-" : hex;
    char line[1024];
    int length = snprintf(line, sizeof(line),
        "MONITOR-RESULT 1 %s outcome=%s exit=%d signal=%d peakRss=%lld maxGapNs=%lld samples=%lld gapLastValidNs=%lld gapNowNs=%lld gapNs=%lld wallNs=%lld cpuNs=%lld locked=%d workerStderrRetained=%zu workerStderrHex=%s monitorThreadCpuDeltaNs=%lld monitorInvCtxSwDelta=%lld\n",
        nonce, result->outcome, result->exit_code, result->signal_number,
        result->peak_rss, result->max_gap_ns, result->samples, result->gap_last_valid_ns, result->gap_now_ns,
        result->gap_ns, result->wall_ns, result->cpu_ns, result->locked,
        result->stderr_count, stderr_hex, result->thread_cpu_delta_ns, result->involuntary_context_switch_delta);
    if (length > 0 && length < (int)sizeof(line)) (void)monitor_report_write(line, (size_t)length);
}

static int monitor_parse_limit_arg(const char *text, long long minimum, long long maximum, long long *value)
{
    if (*text == '\0') return 0;
    long long parsed = 0;
    for (const char *cursor = text; *cursor != '\0'; cursor++) {
        if (*cursor < '0' || *cursor > '9' || parsed > (LLONG_MAX - 9) / 10) return 0;
        parsed = parsed * 10 + (*cursor - '0');
    }
    if (parsed < minimum || parsed > maximum) return 0;
    *value = parsed;
    return 1;
}

static void monitor_prefault(void)
{
    volatile unsigned char stack[16384];
    for (size_t i = 0; i < sizeof(stack); i += 4096) stack[i] = (unsigned char)i;
}

static int monitor_close_child_fds(int first_keep, int second_keep)
{
    if (first_keep < 3 || second_keep < 3 || first_keep == second_keep) return 0;
    unsigned int low = (unsigned int)(first_keep < second_keep ? first_keep : second_keep);
    unsigned int high = (unsigned int)(first_keep < second_keep ? second_keep : first_keep);
    if (low > 3U && syscall(SYS_close_range, 3U, low - 1U, 0) != 0) return 0;
    if (high > low + 1U && syscall(SYS_close_range, low + 1U, high - 1U, 0) != 0) return 0;
    return syscall(SYS_close_range, high + 1U, ~0U, 0) == 0;
}

static void monitor_child_exec(int self_fd, int sync_read, int stderr_write, int worker_argc, char **worker_argv, char *program)
{
    pid_t parent = getppid();
    if (parent <= 1 || prctl(PR_SET_PDEATHSIG, SIGKILL, 0, 0, 0) != 0 || getppid() != parent) _exit(126);
    if (dup2(stderr_write, STDERR_FILENO) != STDERR_FILENO) _exit(126);
    if (stderr_write != STDERR_FILENO) close(stderr_write);
    if (!monitor_close_child_fds(self_fd, sync_read)) _exit(126);
    unsigned char release;
    ssize_t count;
    do { count = read(sync_read, &release, 1); } while (count < 0 && errno == EINTR);
    if (count != 1 || release != 1) _exit(126);
    close(sync_read);
    if (worker_argc < 1 || worker_argc > 32) _exit(126);
    char *child_argv[34];
    child_argv[0] = program;
    for (int i = 0; i < worker_argc; i++) child_argv[i + 1] = worker_argv[i];
    child_argv[worker_argc + 1] = NULL;
    char *const child_env[] = { NULL };
    fexecve(self_fd, child_argv, child_env);
    _exit(126);
}

static void monitor_store_stderr(struct monitor_result *result, int fd)
{
    unsigned char byte;
    ssize_t count = read(fd, &byte, 1);
    if (count <= 0) return;
    if (result->stderr_count < MONITOR_STDERR_RETAIN) result->stderr_bytes[result->stderr_count++] = byte;
    while (result->stderr_count < MONITOR_STDERR_RETAIN) {
        count = read(fd, &byte, 1);
        if (count <= 0) break;
        result->stderr_bytes[result->stderr_count++] = byte;
    }
}

static int monitor_main(int argc, char **argv)
{
    extern char **environ;
    if (environ[0] != NULL) _exit(78);
    if (argc < 8 || strcmp(argv[1], "--monitor") != 0) return 0;
    struct monitor_limits limits;
    if (!monitor_parse_limit_arg(argv[2], 1, 120LL * 1000000000LL, &limits.wall_ns) ||
        !monitor_parse_limit_arg(argv[3], 1, 60LL * 1000000000LL, &limits.cpu_ns) ||
        !monitor_parse_limit_arg(argv[4], 1, 256LL * 1024LL * 1024LL, &limits.resident_bytes) ||
        strcmp(argv[5], "--") != 0 || strlen(argv[6]) != 32)
        unsupported("InvalidMonitorArguments");
    const char *nonce = argv[6];
    pid_t parent = getppid();
    if (parent <= 1 || prctl(PR_SET_PDEATHSIG, SIGKILL, 0, 0, 0) != 0 || getppid() != parent)
        unsupported("ParentDeathProtectionUnavailable");
    int locked = mlockall(MCL_CURRENT | MCL_FUTURE) == 0 ? 1 : 0;
    monitor_prefault();
    int self_fd = open("/proc/self/exe", O_RDONLY | O_CLOEXEC);
    if (self_fd < 0) unsupported("MonitorSelfUnavailable");
    int sync_pipe[2];
    int stderr_pipe[2];
    if (pipe2(sync_pipe, O_CLOEXEC) != 0 || pipe2(stderr_pipe, O_CLOEXEC | O_NONBLOCK) != 0)
        unsupported("MonitorPipeUnavailable");
    pid_t child = fork();
    if (child < 0) unsupported("MonitorForkUnavailable");
    if (child == 0) {
        close(sync_pipe[1]);
        close(stderr_pipe[0]);
        monitor_child_exec(self_fd, sync_pipe[0], stderr_pipe[1], argc - 6, argv + 6, argv[0]);
    }
    close(sync_pipe[0]);
    close(stderr_pipe[1]);
    char path[64];
    snprintf(path, sizeof(path), "/proc/%d/stat", (int)child);
    int stat_fd = open(path, O_RDONLY | O_CLOEXEC);
    int pidfd = (int)syscall(SYS_pidfd_open, child, 0);
    if (stat_fd < 0 || pidfd < 0) {
        monitor_kill_child(child, pidfd);
        monitor_reap_child(child);
        unsupported("WorkerObservationUnavailable");
    }
    long ticks = sysconf(_SC_CLK_TCK);
    long page_size = sysconf(_SC_PAGESIZE);
    if (ticks <= 0 || ticks > 1000000 || page_size <= 0) {
        monitor_kill_child(child, pidfd);
        monitor_reap_child(child);
        unsupported("WorkerObservationUnavailable");
    }
    struct monitor_stat first_stat;
    if (!monitor_read_stat(stat_fd, &first_stat)) {
        monitor_kill_child(child, pidfd);
        monitor_reap_child(child);
        unsupported("WorkerObservationUnavailable");
    }
    char line[256];
    int line_length = snprintf(line, sizeof(line), "MONITOR 1 %s %d %lld\n", nonce, (int)child, first_stat.start_time);
    if (line_length <= 0 || line_length >= (int)sizeof(line) ||
        !monitor_full_write(STDOUT_FILENO, line, (size_t)line_length)) {
        monitor_kill_child(child, pidfd);
        monitor_reap_child(child);
        _exit(78);
    }
    close(STDOUT_FILENO);
    struct monitor_result result = {
        .outcome = "MonitorFailure", .exit_code = -1, .signal_number = 0,
        .locked = locked
    };
    if (!monitor_read_exact_ack()) {
        monitor_kill_child(child, pidfd);
        monitor_reap_child(child);
        result.outcome = "MonitorFailure";
        monitor_report(nonce, &result);
        _exit(78);
    }
    close(STDIN_FILENO);
    long long start_ns = monitor_now_ns();
    unsigned char release = 1;
    if (!monitor_full_write(sync_pipe[1], (const char *)&release, 1)) {
        monitor_kill_child(child, pidfd);
        monitor_reap_child(child);
        result.outcome = "MonitorFailure";
        monitor_report(nonce, &result);
        _exit(78);
    }
    close(sync_pipe[1]);
    long long last_ns = start_ns;
    long long next_ns = start_ns + 1000000LL;
    long long cpu_start = monitor_thread_cpu_ns();
    struct rusage usage_start;
    memset(&usage_start, 0, sizeof(usage_start));
    (void)getrusage(RUSAGE_THREAD, &usage_start);
    long long cpu_last = cpu_start;
    long long invcsw_last = usage_start.ru_nivcsw;
    for (;;) {
        struct timespec deadline = { .tv_sec = next_ns / 1000000000LL, .tv_nsec = next_ns % 1000000000LL };
        int sleep_result;
        while ((sleep_result = clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME, &deadline, NULL)) == EINTR) { }
        long long now = monitor_now_ns();
        long long gap = now - last_ns;
        if (gap > result.max_gap_ns) result.max_gap_ns = gap;
        if (gap < 0 || gap > MONITOR_GAP_NS) {
            result.outcome = "WorkerObservationGap";
            result.gap_last_valid_ns = last_ns - start_ns;
            result.gap_now_ns = now - start_ns;
            result.gap_ns = gap;
            struct rusage usage_gap;
            memset(&usage_gap, 0, sizeof(usage_gap));
            (void)getrusage(RUSAGE_THREAD, &usage_gap);
            result.thread_cpu_delta_ns = monitor_thread_cpu_ns() - cpu_last;
            result.involuntary_context_switch_delta = usage_gap.ru_nivcsw - invcsw_last;
            monitor_kill_child(child, pidfd);
            break;
        }
        unsigned char probe;
        ssize_t stderr_count = read(stderr_pipe[0], &probe, 1);
        if (stderr_count > 0) {
            result.stderr_bytes[result.stderr_count++] = probe;
            monitor_store_stderr(&result, stderr_pipe[0]);
            result.outcome = "MonitorFailure";
            monitor_kill_child(child, pidfd);
            break;
        }
        siginfo_t info;
        memset(&info, 0, sizeof(info));
        int wait_result;
        do { wait_result = waitid(P_PIDFD, (id_t)pidfd, &info, WEXITED | WNOHANG | WNOWAIT); }
        while (wait_result != 0 && errno == EINTR);
        if (wait_result != 0) {
            result.outcome = "MonitorFailure";
            monitor_kill_child(child, pidfd);
            break;
        }
        if (info.si_pid != 0) {
            if (info.si_code == CLD_EXITED) {
                result.outcome = "Exited";
                result.exit_code = info.si_status;
            } else {
                result.outcome = "WorkerSignaled";
                result.signal_number = info.si_status;
            }
            break;
        }
        struct monitor_stat stat;
        if (!monitor_read_stat(stat_fd, &stat)) {
            result.outcome = "MonitorFailure";
            monitor_kill_child(child, pidfd);
            break;
        }
        long long completed = monitor_now_ns();
        gap = completed - last_ns;
        if (gap > result.max_gap_ns) result.max_gap_ns = gap;
        if (gap < 0 || gap > MONITOR_GAP_NS) {
            result.outcome = "WorkerObservationGap";
            result.gap_last_valid_ns = last_ns - start_ns;
            result.gap_now_ns = completed - start_ns;
            result.gap_ns = gap;
            struct rusage usage_gap;
            memset(&usage_gap, 0, sizeof(usage_gap));
            (void)getrusage(RUSAGE_THREAD, &usage_gap);
            result.thread_cpu_delta_ns = monitor_thread_cpu_ns() - cpu_last;
            result.involuntary_context_switch_delta = usage_gap.ru_nivcsw - invcsw_last;
            monitor_kill_child(child, pidfd);
            break;
        }
        last_ns = completed;
        result.samples++;
        cpu_last = monitor_thread_cpu_ns();
        struct rusage usage_last;
        memset(&usage_last, 0, sizeof(usage_last));
        (void)getrusage(RUSAGE_THREAD, &usage_last);
        invcsw_last = usage_last.ru_nivcsw;
        long long rss = stat.rss_pages * (long long)page_size;
        if (stat.rss_pages > LLONG_MAX / page_size || stat.utime > LLONG_MAX - stat.stime) {
            result.outcome = "MonitorFailure";
            monitor_kill_child(child, pidfd);
            break;
        }
        long long clocks = stat.utime + stat.stime;
        long long cpu = (clocks / ticks) * 1000000000LL + (clocks % ticks) * 1000000000LL / ticks;
        if (rss == 0) {
            memset(&info, 0, sizeof(info));
            do { wait_result = waitid(P_PIDFD, (id_t)pidfd, &info, WEXITED | WNOHANG | WNOWAIT); }
            while (wait_result != 0 && errno == EINTR);
            if (wait_result != 0) {
                result.outcome = "MonitorFailure";
                monitor_kill_child(child, pidfd);
                break;
            }
            if (info.si_pid != 0) {
                if (info.si_code == CLD_EXITED) {
                    result.outcome = "Exited";
                    result.exit_code = info.si_status;
                } else {
                    result.outcome = "WorkerSignaled";
                    result.signal_number = info.si_status;
                }
                break;
            }
        }
        result.wall_ns = completed - start_ns;
        result.cpu_ns = cpu;
        if (rss > result.peak_rss) result.peak_rss = rss;
        if (rss > limits.resident_bytes) {
            result.outcome = "WorkerResidentBytes";
            monitor_kill_child(child, pidfd);
            break;
        }
        if (cpu > limits.cpu_ns) {
            result.outcome = "WorkerCpuTime";
            monitor_kill_child(child, pidfd);
            break;
        }
        if (result.wall_ns > limits.wall_ns) {
            result.outcome = "WorkerWallTime";
            monitor_kill_child(child, pidfd);
            break;
        }
        next_ns += 1000000LL;
        while (next_ns <= completed) next_ns += 1000000LL;
    }
    monitor_store_stderr(&result, stderr_pipe[0]);
    monitor_reap_child(child);
    struct rusage usage_end;
    memset(&usage_end, 0, sizeof(usage_end));
    (void)getrusage(RUSAGE_THREAD, &usage_end);
    if (strcmp(result.outcome, "WorkerObservationGap") != 0) {
        result.thread_cpu_delta_ns = monitor_thread_cpu_ns() - cpu_start;
        result.involuntary_context_switch_delta = usage_end.ru_nivcsw - usage_start.ru_nivcsw;
    }
    if (result.wall_ns == 0) result.wall_ns = monitor_now_ns() - start_ns;
    monitor_report(nonce, &result);
    close(stat_fd);
    close(pidfd);
    close(stderr_pipe[0]);
    return strcmp(result.outcome, "Exited") == 0 && result.exit_code == 0 && result.stderr_count == 0 ? 0 : 1;
}

#endif
