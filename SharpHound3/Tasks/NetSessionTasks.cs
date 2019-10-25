﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using SharpHound3.Enums;
using SharpHound3.JSON;
using SharpHound3.LdapWrappers;

namespace SharpHound3.Tasks
{
    internal class NetSessionTasks
    {
        internal static async Task<LdapWrapper> ProcessNetSessions(LdapWrapper wrapper)
        {
            if (wrapper is Computer computer && !computer.PingFailed)
            {
                var sessions = await GetNetSessions(computer);
                var temp = computer.Sessions.ToList();
                temp.AddRange(sessions);
                computer.Sessions = temp.Distinct().ToArray();
            }

            return wrapper;
        }

        private static async Task<List<Session>> GetNetSessions(Computer computer)
        {
            var resumeHandle = IntPtr.Zero;
            var sessionInfoType = typeof(SESSION_INFO_10);

            var entriesRead = 0;
            var ptrInfo = IntPtr.Zero;

            var sessionList = new List<Session>();

            try
            {
                var task = Task.Run(() => NetSessionEnum(computer.APIName, null, null, 10,
                    out ptrInfo, -1, out entriesRead, out _, ref resumeHandle));

                var success = task.Wait(TimeSpan.FromSeconds(10));

                if (!success)
                    return sessionList;

                var taskResult = task.Result;

                if (taskResult != 0)
                {
                    if (Options.Instance.DumpComputerStatus)
                        OutputTasks.AddComputerStatus(new ComputerStatus
                        {
                            ComputerName = computer.DisplayName,
                            Status = ((NET_API_STATUS) taskResult).ToString(),
                            Task = "NetSessionEnum"
                        });
                    return sessionList;
                }
                    

                var sessions = new SESSION_INFO_10[entriesRead];
                var iterator = ptrInfo;

                for (var i = 0; i < entriesRead; i++)
                {
                    sessions[i] = (SESSION_INFO_10) Marshal.PtrToStructure(iterator, sessionInfoType);
                    iterator = (IntPtr) (iterator.ToInt64() + Marshal.SizeOf(sessionInfoType));
                }

                if (Options.Instance.DumpComputerStatus)
                    OutputTasks.AddComputerStatus(new ComputerStatus
                    {
                        ComputerName = computer.DisplayName,
                        Status = "Success",
                        Task = "NetSessionEnum"
                    });

                foreach (var session in sessions)
                {
                    var sessionUsername = session.sesi10_username;
                    var computerName = session.sesi10_cname;
                    
                    if (computerName == null)
                        continue;

                    string computerSid = null;

                    //Filter out computer accounts, Anonymous Logon, empty users
                    if (sessionUsername.EndsWith(
                        "$") || sessionUsername.Trim() == "" || sessionUsername == "$" || sessionUsername ==
                            Options.Instance.CurrentUserName || sessionUsername == "ANONYMOUS LOGON")
                    {
                        continue;
                    }

                    //Remove leading backslashes
                    if (computerName.StartsWith("\\"))
                        computerName = computerName.TrimStart('\\');

                    //If the session is pointing to localhost, we already know what the SID of the computer is
                    if (computerName.Equals("[::1]") || computerName.Equals("127.0.0.1"))
                        computerSid = computer.ObjectIdentifier;

                    //Try converting the computer name to a SID
                    computerSid = computerSid ?? await Helpers.TryResolveHostToSid(computerName, computer.Domain);

                    //Try converting the username to a SID
                    var searcher = Helpers.GetDirectorySearcher(computer.Domain);
                    var sids = searcher.LookupUserInGC(sessionUsername);
                    if (sids.Length > 0)
                    {
                        foreach (var sid in sids)
                        {
                            sessionList.Add(new Session
                            {
                                ComputerId = computerSid,
                                UserId = sid
                            });
                        }
                    }
                    else
                    {
                        var (sidSuccess, userSid) =
                            await Helpers.AccountNameToSid(sessionUsername, computer.Domain, false);
                        if (sidSuccess)
                        {
                            sessionList.Add(new Session
                            {
                                ComputerId = computerSid,
                                UserId = userSid
                            });
                        }
                        else
                        {
                            sessionList.Add(new Session
                            {
                                ComputerId = computerSid,
                                UserId = sessionUsername
                            });
                        }
                    }
                }

                return sessionList;
            }
            finally
            {
                if (ptrInfo != IntPtr.Zero)
                    NetApiBufferFree(ptrInfo);
            }
        }

        #region NetSessionEnum

        [DllImport("NetAPI32.dll", SetLastError = true)]
        private static extern int NetSessionEnum(
            [MarshalAs(UnmanagedType.LPWStr)] string ServerName,
            [MarshalAs(UnmanagedType.LPWStr)] string UncClientName,
            [MarshalAs(UnmanagedType.LPWStr)] string UserName,
            int Level,
            out IntPtr bufptr,
            int prefmaxlen,
            out int entriesread,
            out int totalentries,
            ref IntPtr resume_handle);

        [StructLayout(LayoutKind.Sequential)]
        public struct SESSION_INFO_10
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string sesi10_cname;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string sesi10_username;
            public uint sesi10_time;
            public uint sesi10_idle_time;
        }

        [DllImport("netapi32.dll")]
        private static extern int NetApiBufferFree(
            IntPtr Buff);
        #endregion
    }
}