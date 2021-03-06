// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Utility
{
    using System;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Text;
    using System.Threading.Tasks;

    public static class ProcessUtility
    {
        public static async Task<ProcessDetail> ExecuteWin32ProcessAsync(string executorPath, string arguments, string workingDirectory, int timeoutInMilliseconds)
        {
            var processDetail = new ProcessDetail { ExecutorPath = executorPath, Arguments = arguments, WorkingDirectory = workingDirectory, };

            // initialize compiler process 
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    FileName = executorPath,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDirectory
                }
            };

            using (process)
            {
                var output = new StringBuilder();
                var error = new StringBuilder();
                process.OutputDataReceived += (s, e) => output.AppendLine(e.Data);
                process.ErrorDataReceived += (s, e) => error.AppendLine(e.Data);
                try
                {
                    process.Start();
                }
                catch (Win32Exception e)
                {
                    throw new Exception(e.Message + ":" + processDetail);
                }

                // Use async mode to avoid child process hung forever, e.g. AppLoc.exe will show GUI if input parameter is invalid 
                // Use async mode to avoid deadlock between WaitForExit and ReadToEnd 
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Wait for process to exit within timeout 
                await process.WaitForExitAsync(timeoutInMilliseconds);

                processDetail.ExitCode = process.ExitCode;
                processDetail.StandardOutput = output.ToString();
                processDetail.StandardError = error.ToString();
                processDetail.ProcessId = process.Id;
                return processDetail;
            }
        }

        public static Task WaitForExitAsync(this Process process, int timeoutInMilliseconds)
        {
            return Task.Run(
                () =>
                {
                    if (!process.WaitForExit(timeoutInMilliseconds))
                    {
                        string processName = string.Empty;
                        Exception killProcessException = null;
                        try
                        {
                            processName = process.ProcessName;
                            process.Kill();
                            process.WaitForExit();
                        }
                        catch (InvalidOperationException e)
                        {
                            killProcessException = e;
                        }
                        catch (Win32Exception e)
                        {
                            killProcessException = e;
                        }
                        throw new TimeoutException(
                            string.Format(
                                "Executing {0} exceeds {1} milliseconds, aborted. {2}",
                                processName,
                                timeoutInMilliseconds,
                                killProcessException == null ? string.Empty : killProcessException.Message));
                    }
                });
        }
    }

}