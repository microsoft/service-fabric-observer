// This is the Capabilities proxy binary that enables the FabricObserver process to successfully execute "ls /proc/[pid]/fd | wc -l" as a normal user. 
// By default, FabricObserver runs as a low privilege user on Linux, but can  successfully execute a command that requires root privilege (sudo user) thanks to Linux Capabilities.

// Build/Run Instructions.
// ***NOTE: This binary has already been built and is deployed as part of FO->Ubuntu during the setup phase of application activation.

// 1. Install libcap library: sudo apt-get install -y libcap-dev
// 2. Compile : gcc elevated_proc_fd.c -lcap -o elevated_proc_fd
// ***NOTE: The following is already taken care of by FO Setup and the related FO utility function that runs this binary. 
// 3. Assign PTRACE and DAC_READ_SEARCH capabilities to elevated_netstat : "sudo setcap CAP_DAC_READ_SEARCH,CAP_SYS_PTRACE+p ./elevated_proc_fd"
// 4. Run elevated_proc_fd as a normal user: ./elevated_proc_fd [pid] (Note: If you pass -1 for PID, this will run lsof and return the count of ALL open fds. lsof takes a long time to complete and may not be useful. That's up to you.)

#include <string.h>
#include <stdio.h>
#include <unistd.h>
#include <sys/capability.h>
#include <sys/prctl.h>
/*
           P'(ambient)     = (file is privileged) ? 0 : P(ambient)

           P'(permitted)   = (P(inheritable) & F(inheritable)) |
                             (F(permitted) & P(bounding)) | P'(ambient)

           P'(effective)   = F(effective) ? P'(permitted) : P'(ambient)
*/

static void set_ambient_caps(int* newcaps, int num_elem)
{
    int i;
    for (i = 0; i < num_elem; i++)
    {
        if (prctl(PR_CAP_AMBIENT, PR_CAP_AMBIENT_RAISE, newcaps[i], 0, 0)) {
            printf("Fail!\n");
            return;
        }
        //printf("Success!\n");
    }
}

int main(int argc, char* argv[])
{
    if (argc < 1)
    {
        printf("You have to supply one argument; a process id for use in ls or -1 which would mean run lsof.");
        return -1;
    }

    char* ls_args[64];

    char procFDParam[32];
    char lsbin[32];
    strcpy(lsbin, "/bin/ls");

    if (strcmp(argv[1], "-1") != 0)
    {
        strcpy(procFDParam, "/proc/");
        strcat(procFDParam, argv[1]);
        strcat(procFDParam, "/fd");

        ls_args[0] = "ls";
        ls_args[1] = procFDParam;
        ls_args[2] = NULL;
    }
    else
    {
        strcpy(lsbin, "/usr/bin/lsof");
        ls_args[0] = lsbin;
        ls_args[1] = NULL;
    }

    cap_value_t newcaps[2] = { CAP_DAC_READ_SEARCH, CAP_SYS_PTRACE };

    //Add capabilities in the Inheritable set.
    cap_t caps = cap_get_proc();
    //printf("Capabilities: %s\n", cap_to_text(caps, NULL));
    cap_set_flag(caps, CAP_INHERITABLE, 2, newcaps, CAP_SET);
    cap_set_proc(caps);
    //printf("Capabilities: %s\n", cap_to_text(caps, NULL));
    cap_free(caps);

    // Set ambient capabilities.    
    set_ambient_caps(newcaps, sizeof(newcaps) / sizeof(newcaps[0]));
    
    // Attribution for the code below: https://github.com/spencertipping/shell-tutorial
    int pipe_fds[2]; // (read_end, write_end)
    pipe(pipe_fds);

    if (fork()) 
    {
        // /bin/ls: replace stdout (fd 1) with the write end of the pipe
        dup2(pipe_fds[1], 1); // alias pipe_fds[1] to fd 1
        close(pipe_fds[1]);   // remove pipe_fds[1] from fd table
        close(pipe_fds[0]);   // explained below
        execv(lsbin, ls_args);
    }
    else 
    {
        // /bin/wc: do the same thing for fd 0
        char* wc_args[] = { "wc", "-l", NULL };
        dup2(pipe_fds[0], 0);
        close(pipe_fds[0]);
        close(pipe_fds[1]);
        execv("/usr/bin/wc", wc_args);
    }
}