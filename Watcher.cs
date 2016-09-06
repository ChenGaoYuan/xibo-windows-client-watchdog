﻿using Microsoft.VisualBasic.Devices;
using Nini.Config;
/*
 * Xibo - Digitial Signage - http://www.xibo.org.uk
 * Copyright (C) 2006 - 2014 Daniel Garner
 *
 * This file is part of Xibo.
 *
 * Xibo is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * any later version. 
 *
 * Xibo is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with Xibo.  If not, see <http://www.gnu.org/licenses/>.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using XiboClientWatchdog.Properties;

namespace XiboClientWatchdog
{
    class Watcher
    {
        public static object _locker = new object();

        // Members to stop the thread
        private bool _forceStop = false;
        private ManualResetEvent _manualReset = new ManualResetEvent(false);

        // Config
        private ArgvConfigSource _config;

        // Event to notify activity
        public delegate void OnNotifyActivityDelegate();
        public event OnNotifyActivityDelegate OnNotifyActivity;

        public delegate void OnNotifyRestartDelegate(string message);
        public event OnNotifyRestartDelegate OnNotifyRestart;

        public delegate void OnNotifyErrorDelegate(string message);
        public event OnNotifyErrorDelegate OnNotifyError;

        private int _notRespondingCounter = 0;

        public Watcher(ArgvConfigSource config)
        {
            _config = config;
        }

        /// <summary>
        /// Stops the thread
        /// </summary>
        public void Stop()
        {
            _forceStop = true;
            _manualReset.Set();
        }

        /// <summary>
        /// Runs the agent
        /// </summary>
        public void Run()
        {
            while (!_forceStop)
            {
                lock (_locker)
                {
                    try
                    {
                        // If we are restarting, reset
                        _manualReset.Reset();

                        if (_forceStop)
                            break;

                        string clientLibrary = _config.Configs["Main"].GetString("library", Settings.Default.ClientLibrary);
                        string processPath = _config.Configs["Main"].GetString("watch-process", Settings.Default.ProcessPath);

                        // Check if my Xibo process is running.
                        Process[] proc = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processPath));

                        if (proc.Length <= 0)
                        {
                            string message = "No active processes";
                            WriteToXiboLog(clientLibrary, message);

                            // Notify message
                            if (OnNotifyRestart != null)
                                OnNotifyRestart(message);

                            startProcess(processPath);
                        }
                        else
                        {
                            // Check the process is responding
                            bool notResponding = false;

                            if (Settings.Default.NotRespondingThreshold > 0)
                            {
                                foreach (Process process in proc)
                                {
                                    if (!process.Responding)
                                    {
                                        _notRespondingCounter++;

                                        if (_notRespondingCounter >= Settings.Default.NotRespondingThreshold)
                                        {
                                            // Kill process
                                            killProcess(process, "Killing process - process UI not responding after " + _notRespondingCounter + " checks.");

                                            // Update flags
                                            _notRespondingCounter = 0;
                                            notResponding = true;
                                        }
                                    }
                                    else
                                    {
                                        // If we detect any responding process, set the counter to 0.
                                        _notRespondingCounter = 0;
                                    }
                                }
                            }

                            if (notResponding)
                            {
                                restartProcess(clientLibrary, processPath, string.Format("Activity threshold exceeded. There are {0} processes", proc.Length));
                            }
                            else
                            {
                                // Check status
                                string status = null;

                                // Look in the Xibo library for the status.json file
                                if (File.Exists(Path.Combine(clientLibrary, "status.json")))
                                {
                                    using (StreamReader reader = new StreamReader(Path.Combine(clientLibrary, "status.json")))
                                    {
                                        status = reader.ReadToEnd();
                                    }
                                }

                                // Compare the last accessed date with the current date and threshold
                                if (string.IsNullOrEmpty(status))
                                    throw new Exception("Unable to find status file in " + clientLibrary);

                                // Load the status file in to a JSON string
                                var dict = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(status);

                                DateTime lastActive = DateTime.Parse(dict["lastActivity"].ToString());

                                // Set up the threshold
                                DateTime threshold = DateTime.Now.AddSeconds(Settings.Default.Threshold * -1.0);

                                if (lastActive < threshold)
                                {
                                    // We need to do something about this - client hasn't checked in recently enough
                                    // Stop any matching exe's (kill them)
                                    foreach (Process process in proc)
                                    {
                                        killProcess(process, "Killing process - activity threshold exceeded");
                                    }

                                    restartProcess(clientLibrary, processPath, string.Format("Activity threshold exceeded. There are {0} processes", proc.Length));
                                }
                                else if (Settings.Default.MemoryThreshold > 0)
                                {
                                    // Check the active memory usage of the processes
                                    bool memoryExceeded = false;
                                    long totalMemory = (long)new ComputerInfo().TotalPhysicalMemory;
                                    float percentUsed = 0;

                                    foreach (Process process in proc)
                                    {
                                        percentUsed = ((float)process.PrivateMemorySize64 / (float)totalMemory) * 100;
                                        if (memoryExceeded || percentUsed > Settings.Default.MemoryThreshold)
                                        {
                                            killProcess(process, "Killing process - memory threshold exceeded");
                                            memoryExceeded = true;
                                        }
                                    }

                                    if (memoryExceeded)
                                        restartProcess(clientLibrary, processPath, string.Format("Memory threshold exceeded. {0} used", percentUsed));
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        if (OnNotifyError != null)
                            OnNotifyError(e.ToString());
                    }

                    if (OnNotifyActivity != null)
                        OnNotifyActivity();

                    // Sleep this thread until the next collection interval
                    _manualReset.WaitOne((int)Settings.Default.PollingInterval * 1000);
                }
            }
        }

        /// <summary>
        /// Start process
        /// </summary>
        /// <param name="processPath"></param>
        private void startProcess(string processPath)
        {
            if (Settings.Default.StartWithCmd)
            {
                try
                {
                    Process process = new Process();
                    ProcessStartInfo info = new ProcessStartInfo();

                    info.CreateNoWindow = true;
                    info.WindowStyle = ProcessWindowStyle.Hidden;
                    info.FileName = "cmd.exe";
                    info.Arguments = "/c start \"player\" \"" + processPath + "\"";

                    process.StartInfo = info;
                    process.Start();
                }
                catch (Exception e)
                {
                    if (OnNotifyError != null)
                        OnNotifyError(e.ToString());
                }
            }
            else
            {
                Process.Start(processPath);
            }
        }

        /// <summary>
        /// Kill process
        /// </summary>
        /// <param name="killProcess"></param>
        private void killProcess(Process killProcess, string message)
        {
            // Notify message
            if (OnNotifyRestart != null)
                OnNotifyRestart(message);

            if (Settings.Default.UseTaskKill)
            {
                using (Process process = new Process())
                {
                    ProcessStartInfo startInfo = new ProcessStartInfo();

                    startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                    startInfo.FileName = "taskkill.exe";
                    // Kill using processId, kill tree, force
                    startInfo.Arguments = "/pid " + killProcess.Id.ToString() + " /t /f";

                    process.StartInfo = startInfo;
                    process.Start();
                }
            }
            else
            {
                killProcess.Kill();
            }

            int sleep = Settings.Default.SleepAfterKillSeconds;

            if (sleep > 0)
                Thread.Sleep(sleep * 1000);
        }

        /// <summary>
        /// Restart Process
        /// </summary>
        /// <param name="clientLibrary"></param>
        /// <param name="processPath"></param>
        /// <param name="message"></param>
        private void restartProcess(string clientLibrary, string processPath, string message)
        {
            // Write message to log
            WriteToXiboLog(clientLibrary, message);

            // Notify message
            if (OnNotifyRestart != null)
                OnNotifyRestart(message);

            // Start the exe's
            startProcess(processPath);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="clientLibrary"></param>
        /// <param name="message"></param>
        private void WriteToXiboLog(string clientLibrary, string message)
        {
            // The log is contained in the library folder
            string _logPath = Path.Combine(clientLibrary, Settings.Default.LogFileName);

            // Open the Text Writer
            using (StreamWriter tw = new StreamWriter(File.Open(string.Format("{0}_{1}", _logPath, DateTime.Now.ToFileTimeUtc().ToString()), FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8))
            {
                tw.WriteLine(string.Format("<trace date=\"{0}\" category=\"{1}\">{2}</trace>", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "error", message));
            }
        }
    }
}
