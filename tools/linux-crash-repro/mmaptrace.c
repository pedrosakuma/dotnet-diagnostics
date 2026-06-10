/*
 * mmaptrace.c — LD_PRELOAD interposer that records the mmap/munmap/mprotect
 * history of a process (and its children) WITHOUT ptrace.
 *
 * Purpose: capture the same "what was mapped where, and when was it unmapped"
 * timeline that dotnet/runtime#128525 asked for, but without strace's ptrace —
 * which on our crash-repro job changes timing AND breaks the ClrMD/ptrace live
 * tests (one ptrace tracer per process), removing the very dump+EventPipe load
 * under which the SampleProfiler crash reproduces. This shim runs in-process,
 * so the full Core suite executes unperturbed while we still get the map
 * history to correlate against the faulting address.
 *
 * Each call is logged as a single atomic write() of a compact line:
 *   <mono_ns> pid=<pid> tid=<tid> mmap(addr=..,len=..,prot=..,flags=..,fd=..) = <ret>
 *   <mono_ns> pid=<pid> tid=<tid> munmap(addr=..,len=..) = <rc>
 *   <mono_ns> pid=<pid> tid=<tid> mprotect(addr=..,len=..,prot=..) = <rc>
 *
 * Design notes:
 *  - We call the kernel directly via syscall(2) instead of dlsym(RTLD_NEXT,...).
 *    This sidesteps the classic dlsym->calloc->mmap reentrancy deadlock and is
 *    strictly faithful to what the kernel saw.
 *  - We faithfully reproduce the libc error convention: a raw syscall returns
 *    -errno in the return register, so we translate that back to MAP_FAILED/-1
 *    plus errno. Skipping this would hand CoreCLR a bogus pointer on a failed
 *    mmap and crash it for the wrong reason.
 *  - A per-thread reentrancy guard keeps the logging path from recursing if any
 *    helper ever maps memory.
 *  - Writes are unbuffered (one write() per event) so the lines around the crash
 *    are already on disk even if the process dies hard.
 *
 * Build: cc -shared -fPIC -O2 -o mmaptrace.so mmaptrace.c
 * Use:   LD_PRELOAD=$PWD/mmaptrace.so MMAPTRACE_LOG=$PWD/mmaptrace.log <cmd>
 */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdint.h>
#include <stddef.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <sys/syscall.h>
#include <time.h>
#include <unistd.h>

static int g_fd = -1;
static __thread int g_in_log;

__attribute__((constructor)) static void mmaptrace_init(void)
{
    const char *path = getenv("MMAPTRACE_LOG");
    if (path && *path)
        g_fd = open(path, O_WRONLY | O_CREAT | O_APPEND | O_CLOEXEC, 0644);
    else
        g_fd = 2; /* stderr */
}

static void emit(const char *buf, int len)
{
    if (g_fd < 0) {
        /* mmap can fire before our constructor (early loader maps); lazily open. */
        const char *path = getenv("MMAPTRACE_LOG");
        g_fd = (path && *path)
                   ? open(path, O_WRONLY | O_CREAT | O_APPEND | O_CLOEXEC, 0644)
                   : 2;
    }
    if (g_fd >= 0 && len > 0) {
        ssize_t w = write(g_fd, buf, (size_t)len);
        (void)w;
    }
}

static long mono_ns(void)
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (long)t.tv_sec * 1000000000L + (long)t.tv_nsec;
}

void *mmap(void *addr, size_t len, int prot, int flags, int fd, off_t off)
{
    /* glibc's syscall() wrapper already returns -1 (== MAP_FAILED) and sets
     * errno on failure, so no manual -errno translation is needed. */
    long r = syscall(SYS_mmap, addr, len, prot, flags, fd, off);
    int saved_errno = errno; /* logging below may call write()/open() etc. */
    if (!g_in_log) {
        g_in_log = 1;
        char b[256];
        int n = snprintf(b, sizeof b,
                         "%ld pid=%d tid=%ld mmap(addr=%p,len=%zu,prot=0x%x,flags=0x%x,fd=%d) = 0x%lx\n",
                         mono_ns(), getpid(), syscall(SYS_gettid), addr, len,
                         (unsigned)prot, (unsigned)flags, fd, (unsigned long)r);
        emit(b, n < (int)sizeof b ? n : (int)sizeof b);
        g_in_log = 0;
    }
    errno = saved_errno; /* the caller must observe the mmap's errno, not ours */
    return (void *)r;
}

/* On LP64 Linux mmap64 is identical to mmap. */
void *mmap64(void *addr, size_t len, int prot, int flags, int fd, off_t off)
    __attribute__((alias("mmap")));

int munmap(void *addr, size_t len)
{
    long rc = syscall(SYS_munmap, addr, len);
    int saved_errno = errno;
    if (!g_in_log) {
        g_in_log = 1;
        char b[160];
        int n = snprintf(b, sizeof b,
                         "%ld pid=%d tid=%ld munmap(addr=%p,len=%zu) = %ld\n",
                         mono_ns(), getpid(), syscall(SYS_gettid), addr, len, rc);
        emit(b, n < (int)sizeof b ? n : (int)sizeof b);
        g_in_log = 0;
    }
    errno = saved_errno;
    return (int)rc;
}

int mprotect(void *addr, size_t len, int prot)
{
    long rc = syscall(SYS_mprotect, addr, len, prot);
    int saved_errno = errno;
    if (!g_in_log) {
        g_in_log = 1;
        char b[176];
        int n = snprintf(b, sizeof b,
                         "%ld pid=%d tid=%ld mprotect(addr=%p,len=%zu,prot=0x%x) = %ld\n",
                         mono_ns(), getpid(), syscall(SYS_gettid), addr, len,
                         (unsigned)prot, rc);
        emit(b, n < (int)sizeof b ? n : (int)sizeof b);
        g_in_log = 0;
    }
    errno = saved_errno;
    return (int)rc;
}
