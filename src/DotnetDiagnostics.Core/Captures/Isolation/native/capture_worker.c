#define _GNU_SOURCE
#include <dlfcn.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <linux/audit.h>
#include <linux/filter.h>
#include <linux/landlock.h>
#include <linux/sched.h>
#include <linux/seccomp.h>
#include <signal.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/prctl.h>
#include <sys/ptrace.h>
#include <sys/resource.h>
#include <sys/socket.h>
#include <sys/syscall.h>
#include <sys/uio.h>
#include <sys/wait.h>
#include <unistd.h>

#if !defined(__linux__) || !defined(__x86_64__)
#error The initial worker supports Linux x86-64 only.
#endif

/* Only stable public SQLite C ABI declarations are needed; no system SQLite
 * installation or alternative provider is used. The host selects e_sqlite3. */
typedef struct sqlite3 sqlite3;
typedef struct sqlite3_stmt sqlite3_stmt;
static int (*sql_open)(const char *, sqlite3 **, int, const char *);
static int (*sql_close)(sqlite3 *);
static int (*sql_config)(int, ...);
static int (*sql_db_config)(sqlite3 *, int, ...);
static int (*sql_limit)(sqlite3 *, int, int);
static int (*sql_authorizer)(sqlite3 *, int (*)(void *, int, const char *, const char *, const char *, const char *), void *);
static void (*sql_progress)(sqlite3 *, int, int (*)(void *), void *);
static void (*sql_interrupt)(sqlite3 *);
static int (*sql_prepare)(sqlite3 *, const char *, int, sqlite3_stmt **, const char **);
static int (*sql_step)(sqlite3_stmt *);
static int (*sql_finalize)(sqlite3_stmt *);
static int (*sql_column_int)(sqlite3_stmt *, int);
static int (*sql_readonly)(sqlite3 *, const char *);
static int64_t (*sql_heap)(int64_t);
static void *(*sql_malloc)(int);
static void (*sql_free)(void *);
static int (*sql_version)(void);
static int (*sql_type)(sqlite3_stmt *, int);
static int (*sql_bytes)(sqlite3_stmt *, int);
static const void *(*sql_blob)(sqlite3_stmt *, int);
static const unsigned char *(*sql_text)(sqlite3_stmt *, int);
static int64_t (*sql_int64)(sqlite3_stmt *, int);
static double (*sql_double)(sqlite3_stmt *, int);
static int (*sql_bind_int64)(sqlite3_stmt *, int, int64_t);
static int (*sql_bind_double)(sqlite3_stmt *, int, double);
static int (*sql_bind_null)(sqlite3_stmt *, int);
static int (*sql_bind_text)(sqlite3_stmt *, int, const char *, int, void (*)(void *));
static int (*sql_bind_blob)(sqlite3_stmt *, int, const void *, int, void (*)(void *));
static int (*sql_reset)(sqlite3_stmt *);
static int (*sql_status)(sqlite3_stmt *, int, int);
static int (*sql_extended_error)(sqlite3 *);
static int admission_active;
static int rebuilding;
static int writable_profile;
static int rebuild_ddl;
static int rebuild_denied_action;
static void admission_error(const char *reason);

static void unsupported(const char *reason)
{
    if (admission_active) admission_error(reason);
    fprintf(stdout, "UNSUPPORTED %s\n", reason);
    fflush(stdout);
    _exit(78);
}

static void require(int condition, const char *reason)
{
    if (!condition) unsupported(reason);
}

static void load_sqlite(const char *path)
{
    void *library = dlopen(path, RTLD_NOW | RTLD_LOCAL);
    if (library == NULL) fprintf(stderr, "Trusted SQLite loader: %s\n", dlerror());
    require(library != NULL, "SqliteLibraryUnavailable");
#define LOAD(member, symbol) do { *(void **)(&member) = dlsym(library, symbol); require(member != NULL, "SqliteCapabilityUnavailable"); } while (0)
    LOAD(sql_open, "sqlite3_open_v2");
    LOAD(sql_close, "sqlite3_close");
    LOAD(sql_config, "sqlite3_config");
    LOAD(sql_db_config, "sqlite3_db_config");
    LOAD(sql_limit, "sqlite3_limit");
    LOAD(sql_authorizer, "sqlite3_set_authorizer");
    LOAD(sql_progress, "sqlite3_progress_handler");
    LOAD(sql_interrupt, "sqlite3_interrupt");
    LOAD(sql_prepare, "sqlite3_prepare_v2");
    LOAD(sql_step, "sqlite3_step");
    LOAD(sql_finalize, "sqlite3_finalize");
    LOAD(sql_column_int, "sqlite3_column_int");
    LOAD(sql_readonly, "sqlite3_db_readonly");
    LOAD(sql_heap, "sqlite3_hard_heap_limit64");
    LOAD(sql_malloc, "sqlite3_malloc");
    LOAD(sql_free, "sqlite3_free");
    LOAD(sql_version, "sqlite3_libversion_number");
    LOAD(sql_type, "sqlite3_column_type");
    LOAD(sql_bytes, "sqlite3_column_bytes");
    LOAD(sql_blob, "sqlite3_column_blob");
    LOAD(sql_text, "sqlite3_column_text");
    LOAD(sql_int64, "sqlite3_column_int64");
    LOAD(sql_double, "sqlite3_column_double");
    LOAD(sql_bind_int64, "sqlite3_bind_int64");
    LOAD(sql_bind_double, "sqlite3_bind_double");
    LOAD(sql_bind_null, "sqlite3_bind_null");
    LOAD(sql_bind_text, "sqlite3_bind_text");
    LOAD(sql_bind_blob, "sqlite3_bind_blob");
    LOAD(sql_reset, "sqlite3_reset");
    LOAD(sql_status, "sqlite3_stmt_status");
    LOAD(sql_extended_error, "sqlite3_extended_errcode");
#undef LOAD
    require(sql_config(1) == 0, "SqliteSingleThreadUnavailable");
    sql_heap(32 * 1024 * 1024);
    require(sql_heap(-1) == 32 * 1024 * 1024, "SqliteHeapLimitUnavailable");
    void *too_large = sql_malloc(32 * 1024 * 1024 + 1);
    require(too_large == NULL, "SqliteHeapLimitNotEnforced");
    sql_free(too_large);
}

static int contain(const char *staging)
{
    DIR *tasks = opendir("/proc/self/task");
    require(tasks != NULL, "ThreadInventoryUnavailable");
    int threads = 0;
    struct dirent *entry;
    errno = 0;
    while ((entry = readdir(tasks)) != NULL)
        if (entry->d_name[0] != '.') threads++;
    require(errno == 0, "ThreadInventoryUnavailable");
    closedir(tasks);
    require(threads == 1, "WorkerMustBeSingleThreaded");
    require(prctl(PR_SET_DUMPABLE, 0, 0, 0, 0) == 0, "DumpProtectionUnavailable");
    require(prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) == 0, "NoNewPrivilegesUnavailable");
    int abi = syscall(SYS_landlock_create_ruleset, NULL, 0, LANDLOCK_CREATE_RULESET_VERSION);
    require(abi >= 3, "LandlockUnavailable");
    struct landlock_ruleset_attr rules = {
        .handled_access_fs = LANDLOCK_ACCESS_FS_EXECUTE | LANDLOCK_ACCESS_FS_WRITE_FILE
            | LANDLOCK_ACCESS_FS_READ_FILE | LANDLOCK_ACCESS_FS_READ_DIR
            | LANDLOCK_ACCESS_FS_REMOVE_DIR | LANDLOCK_ACCESS_FS_REMOVE_FILE
            | LANDLOCK_ACCESS_FS_MAKE_CHAR | LANDLOCK_ACCESS_FS_MAKE_DIR
            | LANDLOCK_ACCESS_FS_MAKE_REG | LANDLOCK_ACCESS_FS_MAKE_SOCK
            | LANDLOCK_ACCESS_FS_MAKE_FIFO | LANDLOCK_ACCESS_FS_MAKE_BLOCK
            | LANDLOCK_ACCESS_FS_MAKE_SYM | LANDLOCK_ACCESS_FS_REFER | LANDLOCK_ACCESS_FS_TRUNCATE
    };
    int rules_fd = syscall(SYS_landlock_create_ruleset, &rules, sizeof(rules), 0);
    require(rules_fd >= 0, "LandlockRulesUnavailable");
    int directory = open(staging, O_PATH | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
    require(directory >= 0, "PrivateStagingUnavailable");
    struct landlock_path_beneath_attr path = {
        .allowed_access = LANDLOCK_ACCESS_FS_READ_FILE | LANDLOCK_ACCESS_FS_READ_DIR,
        .parent_fd = directory
    };
    if (writable_profile)
        path.allowed_access |= LANDLOCK_ACCESS_FS_WRITE_FILE | LANDLOCK_ACCESS_FS_MAKE_REG
            | LANDLOCK_ACCESS_FS_REMOVE_FILE | LANDLOCK_ACCESS_FS_TRUNCATE;
    require(syscall(SYS_landlock_add_rule, rules_fd, LANDLOCK_RULE_PATH_BENEATH, &path, 0) == 0,
        "LandlockRuleUnavailable");
    require(syscall(SYS_landlock_restrict_self, rules_fd, 0) == 0, "LandlockInstallFailed");
    close(directory);
    close(rules_fd);

    /* No CLR or worker threads exist. All process/thread creation and exec,
     * networking, signal/process access, namespaces, BPF and io_uring are
     * absent from this default-deny syscall allowlist. Reject other ABIs too. */
#define ALLOW(number) BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, number, 0, 1), BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ALLOW)
    struct sock_filter instructions[] = {
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, arch)),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, AUDIT_ARCH_X86_64, 1, 0),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_KILL_PROCESS),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, nr)),
        ALLOW(SYS_read), ALLOW(SYS_write), ALLOW(SYS_close), ALLOW(SYS_fstat),
        ALLOW(SYS_newfstatat), ALLOW(SYS_stat), ALLOW(SYS_lstat),
        ALLOW(SYS_openat), ALLOW(SYS_open), ALLOW(SYS_lseek), ALLOW(SYS_pread64),
        ALLOW(SYS_readlink), ALLOW(SYS_readlinkat), ALLOW(SYS_getcwd),
        ALLOW(SYS_mmap), ALLOW(SYS_mprotect), ALLOW(SYS_munmap), ALLOW(SYS_mremap),
        ALLOW(SYS_brk), ALLOW(SYS_madvise), ALLOW(SYS_futex), ALLOW(SYS_getrandom),
        ALLOW(SYS_clock_gettime), ALLOW(SYS_clock_nanosleep), ALLOW(SYS_nanosleep),
        ALLOW(SYS_rt_sigaction), ALLOW(SYS_rt_sigprocmask), ALLOW(SYS_rt_sigreturn),
        ALLOW(SYS_getpid), ALLOW(SYS_gettid), ALLOW(SYS_getuid),
        ALLOW(SYS_geteuid), ALLOW(SYS_getgid), ALLOW(SYS_getegid), ALLOW(SYS_getrusage),
        ALLOW(SYS_exit), ALLOW(SYS_exit_group),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, SYS_pwrite64, 0, 1),
        BPF_STMT(BPF_RET | BPF_K, writable_profile ? SECCOMP_RET_ALLOW : SECCOMP_RET_ERRNO | EACCES),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, SYS_fsync, 0, 1),
        BPF_STMT(BPF_RET | BPF_K, writable_profile ? SECCOMP_RET_ALLOW : SECCOMP_RET_ERRNO | EACCES),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, SYS_fdatasync, 0, 1),
        BPF_STMT(BPF_RET | BPF_K, writable_profile ? SECCOMP_RET_ALLOW : SECCOMP_RET_ERRNO | EACCES),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, SYS_ftruncate, 0, 1),
        BPF_STMT(BPF_RET | BPF_K, writable_profile ? SECCOMP_RET_ALLOW : SECCOMP_RET_ERRNO | EACCES),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, SYS_unlink, 0, 1),
        BPF_STMT(BPF_RET | BPF_K, writable_profile ? SECCOMP_RET_ALLOW : SECCOMP_RET_ERRNO | EACCES),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, SYS_unlinkat, 0, 1),
        BPF_STMT(BPF_RET | BPF_K, writable_profile ? SECCOMP_RET_ALLOW : SECCOMP_RET_ERRNO | EACCES),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, SYS_fcntl, 1, 0),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | EACCES),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, args[1])),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, F_SETLK, 2, 0),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, F_GETLK, 1, 0),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | EACCES),
        BPF_STMT(BPF_RET | BPF_K, writable_profile ? SECCOMP_RET_ALLOW : SECCOMP_RET_ERRNO | EACCES),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | EACCES)
    };
#undef ALLOW
    struct sock_fprog filter = { .len = sizeof(instructions) / sizeof(instructions[0]), .filter = instructions };
    require(syscall(SYS_seccomp, SECCOMP_SET_MODE_FILTER, 0, &filter) == 0, "SeccompUnavailable");
    return abi;
}

static void denied(long result, const char *reason)
{
    require(result == -1 && errno == EACCES, reason);
}

static int probe_denials(const char *marker, int helper, uintptr_t address, const char *self)
{
    denied(open(marker, O_RDONLY), "UnrelatedReadAllowed");
    denied(open(marker, O_WRONLY | O_TRUNC), "UnrelatedWriteAllowed");
    char proc[80];
    require(snprintf(proc, sizeof(proc), "/proc/%d/mem", helper) > 0, "ProbeInvalid");
    denied(open(proc, O_RDONLY), "ProcessMemoryFileAllowed");
    denied(socket(AF_INET, SOCK_STREAM, 0), "InetSocketAllowed");
    denied(socket(AF_INET6, SOCK_STREAM, 0), "Inet6SocketAllowed");
    denied(socket(AF_UNIX, SOCK_STREAM, 0), "UnixSocketAllowed");
    denied(socket(AF_NETLINK, SOCK_RAW, 0), "NetlinkSocketAllowed");
    int pair[2];
    denied(socketpair(AF_UNIX, SOCK_STREAM, 0, pair), "SocketPairAllowed");
    denied(syscall(SYS_pidfd_open, helper, 0), "PidfdAllowed");
    char value = 0;
    struct iovec local = { .iov_base = &value, .iov_len = 1 };
    struct iovec remote = { .iov_base = (void *)address, .iov_len = 1 };
    denied(process_vm_readv(helper, &local, 1, &remote, 1, 0), "ProcessReadAllowed");
    denied(process_vm_writev(helper, &local, 1, &remote, 1, 0), "ProcessWriteAllowed");
    denied(ptrace(PTRACE_PEEKDATA, helper, (void *)address, NULL), "PtraceAllowed");
    denied(kill(helper, 0), "SignalAccessAllowed");
    /* If a regression permits a child, it exits immediately, never escapes. */
    long child = syscall(SYS_fork);
    if (child == 0) _exit(90);
    denied(child, "ForkAllowed");
    child = syscall(SYS_clone, SIGCHLD, NULL, NULL, NULL, 0);
    if (child == 0) _exit(90);
    denied(child, "CloneAllowed");
    struct clone_args args = { .exit_signal = SIGCHLD };
    child = syscall(SYS_clone3, &args, sizeof(args));
    if (child == 0) _exit(90);
    denied(child, "Clone3Allowed");
    child = syscall(SYS_vfork);
    if (child == 0) _exit(90);
    denied(child, "VforkAllowed");
    char *const arguments[] = { (char *)self, NULL };
    char *const environment[] = { NULL };
    denied(execve(self, arguments, environment), "ExecAllowed");
    denied(syscall(SYS_execveat, AT_FDCWD, self, arguments, environment, 0), "ExecAtAllowed");
    denied(syscall(SYS_unshare, CLONE_NEWUSER), "NamespaceAllowed");
    denied(syscall(SYS_io_uring_setup, 1, NULL), "IoUringAllowed");
    for (int fd = 3; fd < 32; fd++) {
        char byte;
        require(read(fd, &byte, 1) == -1 && errno == EBADF, "InheritedHandleRetained");
    }
    return 21;
}

static int configuring;
static int64_t instructions;
static int interrupt_next;
static int denied_authorizations;
static sqlite3 *active;
static int64_t instruction_limit = 200000000;
static sqlite3_stmt *executing_statement;
static int admission_authorize(int action, const char *first, const char *second);
static int rebuild_authorize(int action, const char *first, const char *second);

static int authorize(void *unused, int action, const char *first, const char *second,
    const char *database, const char *origin)
{
    (void)unused; (void)second; (void)database; (void)origin;
    if (rebuilding) return rebuild_authorize(action, first, second);
    if (admission_active) return admission_authorize(action, first, second);
    if (action == 21 || action == 33) return 0; /* SELECT / recursive SELECT */
    if (action == 20 && first != NULL && strcmp(first, "probe") == 0) return 0;
    if (configuring && action == 19 && first != NULL &&
        (strcmp(first, "cache_size") == 0 || strcmp(first, "mmap_size") == 0)) return 0;
    denied_authorizations++;
    return 1; /* SQLITE_DENY: no writes, attach, extensions, arbitrary functions. */
}

static int progress(void *unused)
{
    (void)unused;
    if (admission_active) {
        if (executing_statement)
            return instructions + sql_status(executing_statement, 4, 0) > instruction_limit;
        instructions += 1000;
        return instructions > instruction_limit;
    }
    instructions += 1000;
    if (interrupt_next) sql_interrupt(active);
    return instructions >= instruction_limit;
}

static int tracked_step(sqlite3_stmt *statement)
{
    if (!admission_active) return sql_step(statement);
    executing_statement = statement;
    int result = sql_step(statement);
    executing_statement = NULL;
    /* Charge short statements too: they may never reach a progress callback. */
    instructions += sql_status(statement, 4, 1);
    if (instructions > instruction_limit) admission_error("Limit.VmInstructions");
    return result;
}

static int scalar(sqlite3 *db, const char *query)
{
    sqlite3_stmt *statement = NULL;
    require(sql_prepare(db, query, -1, &statement, NULL) == 0, "SqlitePrepareFailed");
    require(tracked_step(statement) == 100, "SqliteStepFailed");
    int result = sql_column_int(statement, 0);
    require(tracked_step(statement) == 101 && sql_finalize(statement) == 0, "SqliteFinishFailed");
    return result;
}

static void execute(sqlite3 *db, const char *query)
{
    sqlite3_stmt *statement = NULL;
    int preparation=sql_prepare(db, query, -1, &statement, NULL);
    if (rebuilding && preparation!=0) {
        char reason[96];
        snprintf(reason,sizeof(reason),"Rebuild.Prepare%d.Authorization%d",sql_extended_error(db),rebuild_denied_action);
        admission_error(reason);
    }
    require(preparation == 0, "SqliteConfigureFailed");
    int result;
    do { result = tracked_step(statement); } while (result == 100);
    if(rebuilding && result!=101) {
        if(result==13) admission_error("Limit.DatabaseBytes");
        char reason[96];
        snprintf(reason,sizeof(reason),"Rebuild.Step%d.Errno%d",sql_extended_error(db),errno);
        admission_error(reason);
    }
    require(result == 101 && sql_finalize(statement) == 0, "SqliteConfigureFailed");
}

static void sql_denied(sqlite3 *db, const char *query)
{
    sqlite3_stmt *statement = NULL;
    int before = denied_authorizations;
    int result = sql_prepare(db, query, -1, &statement, NULL);
    if (statement != NULL) sql_finalize(statement);
    /* Function denial reports SQLITE_ERROR rather than SQLITE_AUTH. In both
     * cases the installed authorizer must actually have denied this request. */
    require((result == 23 || result == 1) && denied_authorizations > before, "SqliteAuthorizerNotEnforced");
}

static sqlite3 *open_configured(const char *uri)
{
    /* URI is generated solely by the trusted Core launcher, never an archive. */
    sqlite3 *db = NULL;
    require(sql_open(uri, &db, (rebuilding ? 0x2 | 0x4 : 0x1) | 0x40 | 0x8000 | 0x40000, NULL) == 0,
        "SqliteOpenFailed");
    active = db;
    require(sql_readonly(db, "main") == (rebuilding ? 0 : 1), "SqliteAccessModeUnavailable");
    const int limits[][2] = { {0, 9 * 1024 * 1024}, {1, 32 * 1024}, {2, 64},
        {3, 32}, {7, 0}, {9, 64}, {10, 0}, {11, 0} };
    for (size_t i = 0; i < sizeof(limits) / sizeof(limits[0]); i++) {
        sql_limit(db, limits[i][0], limits[i][1]);
        require(sql_limit(db, limits[i][0], -1) == limits[i][1], "SqliteLimitUnavailable");
    }
    const int modes[][2] = { {1017, 0}, {1010, 1}, {1005, 0} };
    for (size_t i = 0; i < sizeof(modes) / sizeof(modes[0]); i++) {
        int value = -1;
        require(sql_db_config(db, modes[i][0], modes[i][1], &value) == 0 && value == modes[i][1],
            "SqliteDefensiveModeUnavailable");
    }
    require(sql_authorizer(db, authorize, NULL) == 0, "SqliteAuthorizerUnavailable");
    sql_progress(db, 1000, progress, NULL);
    configuring = 1;
    execute(db, "PRAGMA cache_size=-8192;");
    execute(db, "PRAGMA mmap_size=0;");
    require(scalar(db, "PRAGMA cache_size;") == -8192 && scalar(db, "PRAGMA mmap_size;") == 0,
        "SqliteCacheLimitUnavailable");
    configuring = 0;
    return db;
}

static int probe_sqlite(const char *uri)
{
    sqlite3 *db = open_configured(uri);
    int value = scalar(db, "SELECT value FROM probe;");
    sql_denied(db, "ATTACH ':memory:' AS other;");
    sql_denied(db, "CREATE TABLE forbidden(value);");
    sql_denied(db, "PRAGMA user_version;");
    sql_denied(db, "SELECT load_extension('forbidden');");
    sqlite3_stmt *statement = NULL;
    require(sql_prepare(db, "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<10000) SELECT x FROM n;",
        -1, &statement, NULL) == 0, "SqliteProgressPrepareFailed");
    interrupt_next = 1;
    int result;
    do { result = sql_step(statement); } while (result == 100);
    require(result == 9 && instructions >= 1000, "SqliteInterruptNotEnforced");
    sql_finalize(statement);
    require(sql_close(db) == 0, "SqliteCloseFailed");
    return value;
}

#include "sqlite_admission.h"
#include "sqlite_rebuild.h"

int main(int argc, char **argv)
{
    extern char **environ;
    require(environ[0] == NULL, "WorkerEnvironmentNotEmpty");
    int admission = argc == 7 && strcmp(argv[6], "--admit") == 0;
    rebuilding = argc == 7 && strcmp(argv[6], "--rebuild") == 0;
    int availability = argc == 7 && strcmp(argv[6], "--available") == 0;
    int profile_probe = argc == 10 && strcmp(argv[9], "--writable-profile-probe") == 0;
    writable_profile = rebuilding || profile_probe;
    if (!admission && !rebuilding && !availability && !profile_probe && argc != 9) unsupported("InvalidProbeArguments");
    for (int i = 1; i < argc; i++)
        require(strlen(argv[i]) <= 4096, "ProbeArgumentTooLong");
    require(strlen(argv[1]) == 32, "InvalidNonce");
    pid_t parent=getppid();
    require(parent>1 && prctl(PR_SET_PDEATHSIG,SIGKILL,0,0,0)==0 && getppid()==parent,
        "ParentDeathProtectionUnavailable");
    struct rlimit cpu = {60, 60}, core = {0, 0}, file = {0, 0}, descriptors = {32, 32};
    if (writable_profile) file.rlim_cur = file.rlim_max = 256LL * 1024 * 1024;
    require(setrlimit(RLIMIT_CPU, &cpu) == 0 && setrlimit(RLIMIT_CORE, &core) == 0
        && setrlimit(RLIMIT_FSIZE, &file) == 0, "ResourceLimitsUnavailable");
    require(syscall(SYS_close_range, 3U, ~0U, 0) == 0, "HandleScrubbingUnavailable");
    require(setrlimit(RLIMIT_NOFILE, &descriptors) == 0, "DescriptorLimitUnavailable");
    require(chdir(argv[3]) == 0, "PrivateStagingUnavailable");
    umask(0077);
    load_sqlite(argv[2]);
    int abi = contain(argv[3]);
    printf("READY 1 %s %d\n", argv[1], abi);
    fflush(stdout);
    /* The host starts mandatory RSS/time observation before releasing SQLite. */
    char go[3];
    require(read(STDIN_FILENO, go, sizeof(go)) == 3 && memcmp(go, "GO\n", 3) == 0, "HandshakeFailed");
    if (availability) {
        printf("AVAILABLE 1\n");
        fflush(stdout);
        return 0;
    }
    if (rebuilding) {
        admission_active = 1;
        rebuild_database(argv[4]);
        return 0;
    }
    if (admission) {
        admission_active = 1;
        admit_database(argv[4], argv[5]);
        return 0;
    }
    int probes = probe_denials(argv[5], atoi(argv[6]), (uintptr_t)strtoull(argv[7], NULL, 16), argv[8]);
    int value = probe_sqlite(argv[4]);
    printf("RESULT 1 %s %d %d %d %lld\n", argv[1], probes, value, sql_version(), (long long)instructions);
    fflush(stdout);
    return 0;
}
