﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using SharpHound3.Enums;
using SharpHound3.JSON;
using SharpHound3.LdapWrappers;

namespace SharpHound3.Tasks
{
    internal class LoggedOnTasks
    {
        private static readonly Regex SidRegex = new Regex(@"S-1-5-21-[0-9]+-[0-9]+-[0-9]+-[0-9]+$", RegexOptions.Compiled);

        internal static async Task<LdapWrapper> ProcessLoggedOn(LdapWrapper wrapper)
        {
            if (wrapper is Computer computer && !computer.PingFailed)
            {
                var sessions = new List<Session>();
                sessions.AddRange(await GetLoggedOnUsersAPI(computer));
                sessions.AddRange(GetLoggedOnUsersRegistry(computer));
                var temp = computer.Sessions.ToList();
                temp.AddRange(sessions);
                computer.Sessions = temp.Distinct().ToArray();
            }

            return wrapper;
        }

        private static async Task<List<Session>> GetLoggedOnUsersAPI(Computer computer)
        {
            var resumeHandle = 0;
            var workstationInfoType = typeof(WKSTA_USER_INFO_1);
            var ptrInfo = IntPtr.Zero;
            var entriesRead  = 0;
            var sessionList = new List<Session>();

            try
            {
                var task = Task.Run(() => NetWkstaUserEnum(computer.APIName, 1, out ptrInfo,
                    -1, out entriesRead, out _, ref resumeHandle));

                var success = task.Wait(TimeSpan.FromSeconds(10));

                if (!success)
                    return sessionList;

                var taskResult = task.Result;
                if (taskResult != 0 && taskResult != 234)
                {
                    if (Options.Instance.DumpComputerStatus)
                        OutputTasks.AddComputerStatus(new ComputerStatus
                        {
                            ComputerName = computer.DisplayName,
                            Status = ((NET_API_STATUS) taskResult).ToString(),
                            Task = "NetWkstaUserEnum"
                        });
                    return sessionList;
                }
                    

                var iterator = ptrInfo;

                if (Options.Instance.DumpComputerStatus)
                    OutputTasks.AddComputerStatus(new ComputerStatus
                    {
                        ComputerName = computer.DisplayName,
                        Status = "Success",
                        Task = "NetWkstaUserEnum"
                    });

                for (var i = 0; i < entriesRead; i++)
                {
                    var data = (WKSTA_USER_INFO_1) Marshal.PtrToStructure(iterator, workstationInfoType);
                    iterator = (IntPtr) (iterator.ToInt64() + Marshal.SizeOf(workstationInfoType));

                    var domain = data.wkui1_logon_domain;
                    var username = data.wkui1_username;

                    //Remove local accounts
                    if (domain.Equals(computer.SamAccountName, StringComparison.CurrentCultureIgnoreCase))
                        continue;
                    
                    //Remove blank accounts and computer accounts
                    if (username.Trim() == "" || username.EndsWith("$"))
                        continue;

                    var (sidSuccess, sid) = await Helpers.AccountNameToSid(username, domain, false);
                    if ( sidSuccess)
                    {
                        sessionList.Add(new Session
                        {
                            UserId = sid,
                            ComputerId = computer.ObjectIdentifier
                        });
                    }
                    else
                    {
                        sessionList.Add(new Session
                        {
                            UserId = $"{username}@{Helpers.NormalizeDomainName(domain)}".ToUpper(),
                            ComputerId = computer.ObjectIdentifier
                        });
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

        private static IEnumerable<Session> GetLoggedOnUsersRegistry(Computer computer)
        {
            if (Options.Instance.NoRegistryLoggedOn)
                yield break;

            IEnumerable<string> filteredKeys;
            try
            {
                var key = RegistryKey.OpenRemoteBaseKey(RegistryHive.Users, computer.APIName);

                filteredKeys = key.GetSubKeyNames().Where(subkey => SidRegex.IsMatch(subkey));
            }
            catch (Exception e)
            {
                if (Options.Instance.DumpComputerStatus)
                    OutputTasks.AddComputerStatus(new ComputerStatus
                    {
                        ComputerName = computer.DisplayName,
                        Status = e.Message,
                        Task = "RegistryLoggedOn"
                    });
                yield break;
            }

            foreach (var sid in filteredKeys)
            {
                yield return new Session
                {
                    ComputerId = computer.ObjectIdentifier,
                    UserId = sid
                };
            }

            if (Options.Instance.DumpComputerStatus)
                OutputTasks.AddComputerStatus(new ComputerStatus
                {
                    ComputerName = computer.DisplayName,
                    Status = "Success",
                    Task = "RegistryLoggedOn"
                });
        }

        #region NetWkstaGetInfo

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WKSTA_USER_INFO_1
        {
            public string wkui1_username;
            public string wkui1_logon_domain;
            public string wkui1_oth_domains;
            public string wkui1_logon_server;
        }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int NetWkstaUserEnum(
            string servername,
            int level,
            out IntPtr bufptr,
            int prefmaxlen,
            out int entriesread,
            out int totalentries,
            ref int resume_handle);

        [DllImport("netapi32.dll")]
        private static extern int NetApiBufferFree(
            IntPtr Buff);

        #endregion
    }
}