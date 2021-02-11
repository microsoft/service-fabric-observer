#!/bin/bash
sudo setcap CAP_DAC_READ_SEARCH,CAP_SYS_PTRACE+p ./elevated_netstat
sudo setcap CAP_DAC_READ_SEARCH,CAP_SYS_PTRACE+p ./elevated_proc_fd
sudo setcap CAP_DAC_READ_SEARCH,CAP_DAC_OVERRIDE+p ./elevated_docker_stats