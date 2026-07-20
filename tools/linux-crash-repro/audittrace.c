/*
 * audittrace.c — LD_AUDIT rtld-audit(7) shim that records dynamic-loader
 * module load/unload activity (dlopen/dlclose and the initial link) WITHOUT
 * ptrace.
 *
 * Purpose: dotnet/runtime#128525 comment
 * https://github.com/dotnet/runtime/issues/128525#issuecomment-4729302419
 * points out a real blind spot in tools/linux-crash-repro/mmaptrace.c: that
 * shim interposes the *public* libc symbols mmap()/munmap()/mprotect(), but
 * the dynamic loader's own dlopen()/dlclose() path calls internal glibc
 * symbols (__mmap/__munmap) that never go through public symbol
 * interposition — so any module load/unload driven unmap is invisible to
 * mmaptrace.so. That is exactly the kind of event that could explain the
 * mystery fault address in issue #128525 (an anonymous, 1MB-aligned region,
 * outside every mmap/munmap line we logged, that looks like a TLS destructor
 * table entry for an already-unloaded module).
 *
 * LD_AUDIT is the mechanism the runtime team suggested instead: ld.so calls
 * la_objopen()/la_objclose() for every module it loads/unloads, regardless of
 * whether that happened via the initial link or a later dlopen()/dlclose().
 * This is pure userspace bookkeeping inside ld.so — no ptrace, no syscall
 * interposition — so like mmaptrace.so it runs in-process and leaves the
 * dump+EventPipe timing that reproduces the crash unperturbed.
 *
 * Each event is one atomic write() of a compact line, using the same
 * CLOCK_MONOTONIC epoch as mmaptrace.so so the two logs can be merged and
 * sorted by timestamp:
 *   <mono_ns> pid=<pid> tid=<tid> objopen  lmid=<n> name=<path> base=0x.. extent=[0x..,0x..) vmas=<n>
 *   <mono_ns> pid=<pid> tid=<tid> objclose name=<path> base=0x.. extent=[0x..,0x..) vmas=<n>
 *   <mono_ns> pid=<pid> tid=<tid> activity flag=<ADD|DELETE|CONSISTENT>
 *
 * The "extent" is the union of every /proc/self/maps VMA whose backing path
 * matches the object being opened/closed (not the object's own PT_LOAD
 * program headers): dl_iterate_phdr — the only portable way to read another
 * object's phdrs from an audit module — does not yet list a module at the
 * point la_objopen() fires for it (confirmed empirically: the freshly-opened
 * object's base address never appears in a dl_iterate_phdr walk performed
 * from inside its own la_objopen callback), so a phdr-based extent silently
 * comes back empty. /proc/self/maps instead reflects real kernel mapping
 * state, which is both more accurate for our purpose (directly comparable to
 * the fault address) and available in both directions: it's already correct
 * right after la_objopen (mapping just completed) and it's still correct
 * inside la_objclose, since ld.so calls la_objclose BEFORE it unmaps the
 * object — this is the last chance to observe the extent that is about to
 * become the "mystery" unmapped region.
 *
 * Design notes (mirrors mmaptrace.c):
 *  - No dynamic allocation is used for logging; a fixed-size table maps the
 *    cookie ld.so hands back at la_objclose() to the name/base we recorded at
 *    la_objopen(), using a lock-free bump index (module loads are rare and
 *    single-digit-per-ms at worst, so a CAS-based table is more than enough).
 *  - /proc/self/maps is read with raw open()/read() (no fopen/getline), and
 *    parsed with sscanf over an in-memory buffer — deliberately avoiding
 *    libc paths that could recurse into the loader's own locks while running
 *    from inside an audit callback.
 *  - Writes are unbuffered (one write() per event) so lines around a crash
 *    are already on disk even if the process dies hard.
 *  - la_objopen must return 0 (no LA_FLG_BINDTO/BINDFROM) — we do not want
 *    la_symbind64 callbacks, which would be invoked on the (extremely hot)
 *    symbol resolution path and would perturb timing far more than the
 *    load/unload events we actually care about.
 *
 * Build: cc -shared -fPIC -O2 -o audittrace.so audittrace.c
 * Use:   LD_AUDIT=$PWD/audittrace.so AUDITTRACE_LOG=$PWD/audittrace.log <cmd>
 *
 * Can be combined with mmaptrace.so (LD_PRELOAD + LD_AUDIT are independent
 * loader mechanisms and do not conflict).
 */
#define _GNU_SOURCE
#include <elf.h>
#include <errno.h>
#include <fcntl.h>
#include <link.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/syscall.h>
#include <time.h>
#include <unistd.h>

static int g_fd = -1;
static __thread int g_in_log;

#define MAX_TRACKED_OBJECTS 4096

typedef struct
{
    uintptr_t base;
    uintptr_t lo;
    uintptr_t hi;
    char name[192];
} tracked_object_t;

static tracked_object_t g_objects[MAX_TRACKED_OBJECTS];
/* Bump allocator: next free slot. Wraps (best effort) if we ever exceed the
 * table — losing the oldest entries is an acceptable degradation for a
 * best-effort repro harness, never a crash or a hang. */
static volatile long g_next_slot;

static void open_log(void)
{
    const char *path = getenv("AUDITTRACE_LOG");
    g_fd = (path && *path)
               ? open(path, O_WRONLY | O_CREAT | O_APPEND | O_CLOEXEC, 0644)
               : 2; /* stderr */
}

static void emit(const char *buf, int len)
{
    if (g_fd < 0)
        open_log();
    if (g_fd >= 0 && len > 0)
    {
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

/* Reads a small chunk of /proc/self/maps into buf. Returns bytes read, or <=0
 * on error/EOF. No libc buffered I/O (fopen/getline) is used here: this can
 * run re-entrantly from inside la_objopen/la_objclose while ld.so's own
 * internal locks are held, and the fewer libc paths we touch the less risk
 * of deadlocking against the loader. */
static ssize_t read_all(int fd, char *buf, size_t cap)
{
    size_t total = 0;
    while (total < cap - 1)
    {
        ssize_t r = read(fd, buf + total, cap - 1 - total);
        if (r < 0)
        {
            if (errno == EINTR)
                continue;
            break;
        }
        if (r == 0)
            break;
        total += (size_t)r;
    }
    buf[total] = '\0';
    return (ssize_t)total;
}

/* Matches by basename, not full path: /proc/self/maps always prints the
 * fully-resolved, symlink-free path (e.g. /usr/lib/x86_64-linux-gnu/ld-
 * linux-x86-64.so.2), while map->l_name frequently records the unresolved
 * search-path form the loader actually opened (e.g. /lib64/ld-linux-x86-64.
 * so.2 on multiarch Debian/Ubuntu) — a full-path suffix match misses these.
 * Matching the final path component is the practical, portable option; the
 * (rare) risk of two different libs sharing a basename under different
 * Lmid namespaces is an acceptable trade-off for a best-effort repro tool. */
static const char *basename_of(const char *path)
{
    const char *slash = strrchr(path, '/');
    return slash ? slash + 1 : path;
}

static int path_matches(const char *maps_path, const char *want)
{
    if (!*want)
        return 0;
    return strcmp(basename_of(maps_path), basename_of(want)) == 0;
}

/* Computes the [lo, hi) union of every /proc/self/maps VMA whose backing
 * path matches `name`. This is deliberately based on live kernel mappings
 * (not the loader's own program-header bookkeeping, which is not yet
 * consistent for a just-opened object — dl_iterate_phdr does not see a
 * module until AFTER la_objopen returns): the whole point of this shim is to
 * compare against real mmap/munmap state, and /proc/self/maps IS that state,
 * for both la_objopen (mapping just completed) and la_objclose (ld.so calls
 * this BEFORE unmapping, so the VMAs are still present). */
static unsigned compute_extent(const char *name, uintptr_t *lo, uintptr_t *hi)
{
    unsigned count = 0;
    *lo = 0;
    *hi = 0;

    if (!name || !*name)
        return 0;

    int fd = open("/proc/self/maps", O_RDONLY | O_CLOEXEC);
    if (fd < 0)
        return 0;

    /* /proc/self/maps for a large process (CoreCLR + JIT) can run past 64K;
     * grow the buffer once if the first read fills it completely. */
    static char buf[262144];
    ssize_t n = read_all(fd, buf, sizeof buf);
    close(fd);
    if (n <= 0)
        return 0;

    char *line = buf;
    while (line && *line)
    {
        char *nl = strchr(line, '\n');
        if (nl)
            *nl = '\0';

        unsigned long start = 0, end = 0;
        char path[512] = {0};
        /* "start-end perms offset dev inode  path" — path may be absent for
         * anonymous mappings, which is fine: sscanf just won't fill path. */
        if (sscanf(line, "%lx-%lx %*s %*s %*s %*s %511s", &start, &end, path) >= 2 &&
            path_matches(path, name))
        {
            if (count == 0 || (uintptr_t)start < *lo)
                *lo = start;
            if (count == 0 || (uintptr_t)end > *hi)
                *hi = end;
            count++;
        }

        line = nl ? nl + 1 : NULL;
    }

    return count;
}

static void safe_copy_name(char *dst, size_t dstlen, const char *src)
{
    if (!src || !*src)
        src = "(main-executable)";
    size_t n = strlen(src);
    if (n >= dstlen)
        n = dstlen - 1;
    memcpy(dst, src, n);
    dst[n] = '\0';
}

unsigned int la_version(unsigned int version)
{
    /* Accept whatever the loader offers, up to what we were built against. */
    return version;
}

unsigned int la_objopen(struct link_map *map, Lmid_t lmid, uintptr_t *cookie)
{
    char match_name[512];
    if (map->l_name && *map->l_name)
    {
        strncpy(match_name, map->l_name, sizeof match_name - 1);
        match_name[sizeof match_name - 1] = '\0';
    }
    else
    {
        /* The main executable's link_map has an empty l_name. Resolve its
         * real path so it still matches against /proc/self/maps entries. */
        ssize_t n = readlink("/proc/self/exe", match_name, sizeof match_name - 1);
        match_name[n > 0 ? n : 0] = '\0';
    }

    uintptr_t lo = 0, hi = 0;
    unsigned vma_count = compute_extent(match_name, &lo, &hi);

    long slot = __atomic_fetch_add(&g_next_slot, 1, __ATOMIC_RELAXED) % MAX_TRACKED_OBJECTS;
    tracked_object_t *obj = &g_objects[slot];
    obj->base = map->l_addr;
    obj->lo = lo;
    obj->hi = hi;
    /* Store the resolved match_name (not map->l_name, which is empty for the
     * main executable) so la_objclose can re-run compute_extent() with the
     * same key that actually matches /proc/self/maps entries. */
    safe_copy_name(obj->name, sizeof obj->name, match_name);
    *cookie = (uintptr_t)(slot + 1); /* 0 is reserved for "not found" */

    if (!g_in_log)
    {
        g_in_log = 1;
        char b[320];
        int n = snprintf(b, sizeof b,
                          "%ld pid=%d tid=%ld objopen  lmid=%ld name=%s base=0x%lx extent=[0x%lx,0x%lx) vmas=%u\n",
                          mono_ns(), getpid(), syscall(SYS_gettid), (long)lmid,
                          obj->name, (unsigned long)obj->base, (unsigned long)lo,
                          (unsigned long)hi, vma_count);
        emit(b, n < (int)sizeof b ? n : (int)sizeof b);
        g_in_log = 0;
    }

    /* No LA_FLG_BINDTO/BINDFROM: we deliberately do not want la_symbind64
     * callbacks on the hot symbol-resolution path. */
    return 0;
}

unsigned int la_objclose(uintptr_t *cookie)
{
    long slot = (long)*cookie - 1;
    const tracked_object_t *obj = NULL;
    if (slot >= 0 && slot < MAX_TRACKED_OBJECTS)
        obj = &g_objects[slot];

    /* Recompute from /proc/self/maps rather than reusing the la_objopen
     * extent verbatim: this is called right before ld.so unmaps the object,
     * so it is our last chance to see the VMAs that are about to become the
     * "mystery unmapped region" — and re-measuring also tolerates any
     * mprotect/remap churn that happened between open and close. */
    uintptr_t lo = 0, hi = 0;
    unsigned vma_count = obj ? compute_extent(obj->name, &lo, &hi) : 0;

    if (!g_in_log)
    {
        g_in_log = 1;
        char b[320];
        int n;
        if (obj)
            n = snprintf(b, sizeof b,
                         "%ld pid=%d tid=%ld objclose name=%s base=0x%lx extent=[0x%lx,0x%lx) vmas=%u\n",
                         mono_ns(), getpid(), syscall(SYS_gettid), obj->name,
                         (unsigned long)obj->base, (unsigned long)lo,
                         (unsigned long)hi, vma_count);
        else
            n = snprintf(b, sizeof b,
                         "%ld pid=%d tid=%ld objclose cookie=0x%lx (unknown slot)\n",
                         mono_ns(), getpid(), syscall(SYS_gettid), (unsigned long)*cookie);
        emit(b, n < (int)sizeof b ? n : (int)sizeof b);
        g_in_log = 0;
    }
    return 0;
}

void la_activity(uintptr_t *cookie, unsigned int flag)
{
    (void)cookie;
    if (!g_in_log)
    {
        g_in_log = 1;
        const char *what = flag == LA_ACT_ADD ? "ADD" : flag == LA_ACT_DELETE ? "DELETE" : "CONSISTENT";
        char b[128];
        int n = snprintf(b, sizeof b, "%ld pid=%d tid=%ld activity flag=%s\n",
                          mono_ns(), getpid(), syscall(SYS_gettid), what);
        emit(b, n < (int)sizeof b ? n : (int)sizeof b);
        g_in_log = 0;
    }
}
