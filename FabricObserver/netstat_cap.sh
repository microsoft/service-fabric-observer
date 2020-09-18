#!/bin/bash
sudo setcap CAP_DAC_READ_SEARCH,CAP_SYS_PTRACE+p ./elevated_netstat
