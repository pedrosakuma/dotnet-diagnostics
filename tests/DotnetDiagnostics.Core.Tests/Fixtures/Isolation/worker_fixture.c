#define _GNU_SOURCE
#include <errno.h>
#include <linux/filter.h>
#include <linux/seccomp.h>
#include <limits.h>
#include <stddef.h>
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>
#include <fcntl.h>
#include <sys/prctl.h>
#include <sys/resource.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <time.h>

static volatile int benign_marker = 123;

static void unsupported(const char *reason)
{
    fprintf(stdout, "UNSUPPORTED %s\n", reason);
    fflush(stdout);
    _exit(78);
}

#include "../../../../src/DotnetDiagnostics.Core/Captures/Isolation/native/worker_monitor.h"

int main(int argc, char **argv)
{
    if (argc > 1 && strcmp(argv[1], "--monitor") == 0) return monitor_main(argc, argv);
    if (argc == 2 && strcmp(argv[1], "helper") == 0) {
        printf("%lx\n", (unsigned long)(uintptr_t)&benign_marker);
        fflush(stdout);
        while (1) pause();
    }
    if (argc != 9) return 2;
    if (strstr(argv[4], "no-landlock") != NULL || strstr(argv[4], "no-seccomp") != NULL) {
        int blocked = strstr(argv[4], "no-landlock") != NULL ? SYS_landlock_create_ruleset : SYS_seccomp;
        struct sock_filter code[] = {
            BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, nr)),
            BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, blocked, 0, 1),
            BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | ENOSYS),
            BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ALLOW)
        };
        struct sock_fprog filter = { .len = 4, .filter = code };
        if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0 ||
            prctl(PR_SET_SECCOMP, SECCOMP_MODE_FILTER, &filter) != 0) return 4;
        argv[0] = argv[8];
        execv(argv[0], argv);
        return 5;
    }
    if (strstr(argv[4], "bad-handshake") != NULL) {
        puts("READY 1 wrong-nonce 7");
        fflush(stdout);
        while (1) pause();
    }
    if (strstr(argv[4], "unsupported") != NULL) {
        puts("UNSUPPORTED PurposeCreatedMissingFacility");
        return 78;
    }
    printf("READY 1 %s 7\n", argv[1]);
    fflush(stdout);
    char go[4];
    if (read(0, go, sizeof(go)) != 3) return 3;
    if (strstr(argv[4], "flood") != NULL) {
        for (int i = 0; i < 10000; i++) putchar('x');
        fflush(stdout);
    }
    if (strstr(argv[4], "crash") != NULL) return 42;
    if (strstr(argv[4], "stderr") != NULL) {
        for (int i = 0; i < 4096; i++) fputc('e', stderr);
        fflush(stderr);
        while (1) pause();
    }
    if (strstr(argv[4], "cpu") != NULL) {
        volatile unsigned long long value = 1;
        while (1) value = value * 6364136223846793005ULL + 1;
    }
    if (strstr(argv[4], "self-gap") != NULL) {
        pid_t monitor = getppid();
        pid_t resumer = fork();
        if (resumer < 0) return 43;
        if (resumer == 0) {
            usleep(30000);
            kill(monitor, SIGCONT);
            _exit(0);
        }
        kill(monitor, SIGSTOP);
        if (strstr(argv[4], "exit") != NULL) return 0;
        while (1) pause();
    }
    if (strstr(argv[4], "exit") != NULL) return 0;
    while (1) pause();
}
