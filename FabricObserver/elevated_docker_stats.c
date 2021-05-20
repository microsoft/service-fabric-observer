// This is used by FO to get resource usage information about docker containers (docker stats), which is an elevated command and FO should not run as root on Linux. 
// We employ Capabilities to solve this problem. This is the proxy binary we run that can call "docker stats" as a normal user.

// Build/Run Instructions.
// ***NOTE: This binary has already been built on Ubuntu 18.04.5 LTS OS machine and is located in this repo: elevated_docker_stats.
// When you run the Build-FabricObserver.ps1 script, all modifications necessary to run FO on Linux are done for you and copied to the output folder.

/* To build yourself:

1. On Ubuntu: Install libcap library: sudo apt-get install -y libcap-dev
2. Compile : gcc elevated_docker_stats.c -lcap -o elevated_docker_stats
3. Assign CAP_DAC_OVERRIDE and DAC_READ_SEARCH capabilities to elevated_docker_stats: sudo setcap CAP_DAC_READ_SEARCH,CAP_DAC_OVERRIDE+p ./elevated_docker_stats
4. Run elevated_docker_stats as a regular user: ./elevated_docker_stats
*/

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

int main()
{
    cap_value_t newcaps[2] = { CAP_DAC_READ_SEARCH,CAP_DAC_OVERRIDE };

    // Add capabilities in the Inheritable set.
    cap_t caps = cap_get_proc();
    //printf("Capabilities: %s\n", cap_to_text(caps, NULL));
    cap_set_flag(caps, CAP_INHERITABLE, 2, newcaps, CAP_SET);
    cap_set_proc(caps);
    //printf("Capabilities: %s\n", cap_to_text(caps, NULL));
    cap_free(caps);

    // Set ambient capabilities.    
    set_ambient_caps(newcaps, sizeof(newcaps) / sizeof(newcaps[0]));

    char* param[] = { "docker", "stats", "--no-stream", "--format", "table {{.Container}}\t{{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}", NULL };
    execv("/usr/bin/docker", param);
}