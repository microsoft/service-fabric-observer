// This is used by FO to get port information given a process id, which is an elevated netstat command. We employ Capabilities
// to solve this problem. This is the proxy binary we run that can call "netstat -tnap" as a normal user.

// Build/Run Instructions.
// ***NOTE: This binary has already been built and is deployed as part of FO->Ubuntu deployment.

// You may need to modify this and compile this yourself depending on where netstat lives on our Ubuntu image. /bin/netstat is generally correct...
// 1. Install libcap library: sudo apt-get install -y libcap-dev
// 2. Compile : gcc elevated_netstat.c -lcap -o elevated_netstat
// ***NOTE: The following is already taken care of by FO Setup and the related FO utility function that runs this binary. 
// 3. Assign PTRACE and DAC_READ_SEARCH capabilities to elevated_netstat : "sudo setcap CAP_DAC_READ_SEARCH,CAP_SYS_PTRACE+p ./elevated_netstat"
// 4. Run elevated_netstat as a regular user : ./elevated_netstat 
//

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

static void set_ambient_caps(int *newcaps, int num_elem)
{
        int i;
        for(i=0; i<num_elem; i++)
        {
                if(prctl(PR_CAP_AMBIENT, PR_CAP_AMBIENT_RAISE, newcaps[i], 0, 0)){
                        printf("Fail!\n");
                        return;
                }
                //printf("Success!\n");
        }
}

int main()
{
        cap_value_t newcaps[2] = {CAP_DAC_READ_SEARCH, CAP_SYS_PTRACE};

        //Add capabilities in the Inheritable set.
        cap_t caps = cap_get_proc();
        //printf("Capabilities: %s\n", cap_to_text(caps, NULL));
        cap_set_flag(caps, CAP_INHERITABLE, 2, newcaps, CAP_SET);
        cap_set_proc(caps);
        //printf("Capabilities: %s\n", cap_to_text(caps, NULL));
        cap_free(caps);

        // Set ambient capabilities.    
        set_ambient_caps(newcaps, sizeof(newcaps)/sizeof(newcaps[0]));
        char *param[] = {"netstat", "-tnap", NULL};
        execv("/bin/netstat", param);
}

