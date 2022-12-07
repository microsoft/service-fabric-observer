// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// Type that holds tcp port info as extracted from supplied netstat output row.
    /// </summary>
    public class TcpPortInfo
    {
        /// <summary>
        /// Local IP address.
        /// </summary>
        public string LocalAddress
        {
            get; private set;
        }

        /// <summary>
        /// Local port number.
        /// </summary>
        public int LocalPort
        {
            get;  private set;
        }

        /// <summary>
        /// Remote IP address.
        /// </summary>
        public string ForeignAddress
        {
            get; private set;
        }

        /// <summary>
        /// Port connection state.
        /// </summary>
        public string State
        {
            get; private set;
        }

        /// <summary>
        /// Owning process id.
        /// </summary>
        public int OwningProcessId
        {
            get; private set;
        }

        /// <summary>
        /// Creates a new instance of TcpPortInfo and set properties based on supplied netstat out row string.
        /// </summary>
        /// <param name="netstatOutputLine">A netstat console output row (string).</param>
        /// <exception cref="ArgumentException">Throws ArgumentException if the specified string is not a netstat output row.</exception>
        public TcpPortInfo(string netstatOutputLine)
        {
            if (string.IsNullOrWhiteSpace(netstatOutputLine))
            {
                throw new ArgumentException("netstatOutputLine value must be a valid nestat output row");
            }

            string[] stats = netstatOutputLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (stats.Length != 5 || !int.TryParse(stats[4], out int pid))
            {
                throw new ArgumentException("netstatOutputLine value must be a valid nestat output row");
            }

            string localIpAndPort = stats[1];

            if (string.IsNullOrWhiteSpace(localIpAndPort) || !localIpAndPort.Contains(":"))
            {
                throw new ArgumentException("netstatOutputLine value must be a valid nestat output row");
            }

            if (!int.TryParse(localIpAndPort.Split(':')[1], out int localPort))
            {
                throw new ArgumentException("netstatOutputLine value must be a valid nestat output row");
            }

            LocalAddress = localIpAndPort;
            LocalPort = localPort;
            ForeignAddress = stats[2];
            State = stats[3];
            OwningProcessId = pid;
        }
    }
}
