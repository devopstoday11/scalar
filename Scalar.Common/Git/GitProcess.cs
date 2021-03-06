using Scalar.Common.FileSystem;
using Scalar.Common.Tracing;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Scalar.Common.Git
{
    public class GitProcess : ICredentialStore
    {
        /// <remarks>
        /// For UnitTest purposes
        /// </remarks>
        public static string ExpireTimeDateString
        {
            get
            {
                if (expireTimeDateString == null)
                {
                    expireTimeDateString = DateTime.Now.Subtract(TimeSpan.FromDays(1)).ToShortDateString();
                }

                return expireTimeDateString;
            }
        }

        private static string expireTimeDateString;

        private const int HResultEHANDLE = -2147024890; // 0x80070006 E_HANDLE

        private static readonly Encoding UTF8NoBOM = new UTF8Encoding(false);
        private static bool failedToSetEncoding = false;

        /// <summary>
        /// Lock taken for duration of running executingProcess.
        /// </summary>
        private object executionLock = new object();

        /// <summary>
        /// Lock taken when changing the running state of executingProcess.
        ///
        /// Can be taken within executionLock.
        /// </summary>
        private object processLock = new object();

        private string gitBinPath;
        private string workingDirectoryRoot;
        private string dotGitRoot;
        private Process executingProcess;
        private bool stopping;

        static GitProcess()
        {
            // If the encoding is UTF8, .Net's default behavior will include a BOM
            // We need to use the BOM-less encoding because Git doesn't understand it
            if (Console.InputEncoding.CodePage == UTF8NoBOM.CodePage)
            {
                try
                {
                    Console.InputEncoding = UTF8NoBOM;
                }
                catch (IOException ex) when (ex.HResult == HResultEHANDLE)
                {
                    // If the standard input for a console is redirected / not available,
                    // then we might not be able to set the InputEncoding here.
                    // In practice, this can happen if we attempt to run a GitProcess from within a Service,
                    // such as Scalar.Service.
                    // Record that we failed to set the encoding, but do not quite the process.
                    // This means that git commands that use stdin will not work, but
                    // for our scenarios, we do not expect these calls at this this time.
                    // We will check and fail if we attempt to write to stdin in in a git call below.
                    GitProcess.failedToSetEncoding = true;
                }
            }
        }

        public GitProcess(Enlistment enlistment)
            : this(enlistment.GitBinPath, enlistment.WorkingDirectoryRoot)
        {
        }

        public GitProcess(string gitBinPath, string workingDirectoryRoot)
        {
            if (string.IsNullOrWhiteSpace(gitBinPath))
            {
                throw new ArgumentException(nameof(gitBinPath));
            }

            this.gitBinPath = gitBinPath;
            this.workingDirectoryRoot = workingDirectoryRoot;

            if (this.workingDirectoryRoot != null)
            {
                this.dotGitRoot = Path.Combine(this.workingDirectoryRoot, ScalarConstants.DotGit.Root);
            }
        }

        public bool LowerPriority { get; set; }

        public static Result Init(Enlistment enlistment)
        {
            return new GitProcess(enlistment).InvokeGitOutsideEnlistment("init \"" + enlistment.WorkingDirectoryRoot + "\"");
        }

        public static Result SparseCheckoutInit(Enlistment enlistment)
        {
            return new GitProcess(enlistment).InvokeGitInWorkingDirectoryRoot("sparse-checkout init --cone", fetchMissingObjects: true);
        }

        public static ConfigResult GetFromGlobalConfig(string gitBinPath, string settingName)
        {
            return new ConfigResult(
                new GitProcess(gitBinPath, workingDirectoryRoot: null).InvokeGitOutsideEnlistment("config --global " + settingName),
                settingName);
        }

        public static ConfigResult GetFromSystemConfig(string gitBinPath, string settingName)
        {
            return new ConfigResult(
                new GitProcess(gitBinPath, workingDirectoryRoot: null).InvokeGitOutsideEnlistment("config --system " + settingName),
                settingName);
        }

        public static bool TryGetVersion(string gitBinPath, out GitVersion gitVersion, out string error)
        {
            GitProcess gitProcess = new GitProcess(gitBinPath, null);
            Result result = gitProcess.InvokeGitOutsideEnlistment("--version");
            string version = result.Output;

            if (result.ExitCodeIsFailure || !GitVersion.TryParseGitVersionCommandResult(version, out gitVersion))
            {
                gitVersion = null;
                error = "Unable to determine installed git version. " + version;
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Tries to kill the run git process.  Make sure you only use this on git processes that can safely be killed!
        /// </summary>
        /// <param name="processName">Name of the running process</param>
        /// <param name="exitCode">Exit code of the kill.  -1 means there was no running process.</param>
        /// <param name="error">Error message of the kill</param>
        /// <returns></returns>
        public bool TryKillRunningProcess(out string processName, out int exitCode, out string error)
        {
            this.stopping = true;
            processName = null;
            exitCode = -1;
            error = null;

            lock (this.processLock)
            {
                Process process = this.executingProcess;

                if (process != null)
                {
                    processName = process.ProcessName;

                    return ScalarPlatform.Instance.TryKillProcessTree(process.Id, out exitCode, out error);
                }

                return true;
            }
        }

        public virtual bool TryDeleteCredential(ITracer tracer, string repoUrl, string username, string password, out string errorMessage)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("url={0}\n", repoUrl);

            // Passing the username and password that we want to signal rejection for is optional.
            // Credential helpers that support it can use the provided username/password values to
            // perform a check that they're being asked to delete the same stored credential that
            // the caller is asking them to erase.
            // Ideally, we would provide these values if available, however it does not work as expected
            // with our main credential helper - Windows GCM. With GCM for Windows, the credential acquired
            // with credential fill for dev.azure.com URLs are not erased when the user name / password are passed in.
            // Until the default credential helper works with this pattern, reject credential with just the URL.

            sb.Append("\n");

            string stdinConfig = sb.ToString();

            Result result = this.InvokeGitOutsideEnlistment(
                GenerateCredentialVerbCommand("reject"),
                stdin => stdin.Write(stdinConfig),
                null);

            if (result.ExitCodeIsFailure)
            {
                tracer.RelatedWarning("Git could not reject credentials: {0}", result.Errors);

                errorMessage = result.Errors;
                return false;
            }

            errorMessage = null;
            return true;
        }

        public virtual bool TryStoreCredential(ITracer tracer, string repoUrl, string username, string password, out string errorMessage)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("url={0}\n", repoUrl);
            sb.AppendFormat("username={0}\n", username);
            sb.AppendFormat("password={0}\n", password);
            sb.Append("\n");

            string stdinConfig = sb.ToString();

            Result result = this.InvokeGitOutsideEnlistment(
                GenerateCredentialVerbCommand("approve"),
                stdin => stdin.Write(stdinConfig),
                null);

            if (result.ExitCodeIsFailure)
            {
                tracer.RelatedWarning("Git could not approve credentials: {0}", result.Errors);

                errorMessage = result.Errors;
                return false;
            }

            errorMessage = null;
            return true;
        }

        /// <summary>
        /// Input for certificate credentials looks like
        /// <code> protocol=cert
        /// path=[http.sslCert value]
        /// username =</code>
        /// </summary>
        public bool TryGetCertificatePassword(
            ITracer tracer,
            string certificatePath,
            out string password,
            out string errorMessage)
        {
            password = null;
            errorMessage = null;

            using (ITracer activity = tracer.StartActivity("TryGetCertificatePassword", EventLevel.Informational))
            {
                Result gitCredentialOutput = this.InvokeGitAgainstDotGitFolder(
                    "credential fill",
                    stdin => stdin.Write("protocol=cert\npath=" + certificatePath + "\nusername=\n\n"),
                    parseStdOutLine: null);

                if (gitCredentialOutput.ExitCodeIsFailure)
                {
                    EventMetadata errorData = new EventMetadata();
                    errorData.Add("CertificatePath", certificatePath);
                    tracer.RelatedWarning(
                        errorData,
                        "Git could not get credentials: " + gitCredentialOutput.Errors,
                        Keywords.Network | Keywords.Telemetry);
                    errorMessage = gitCredentialOutput.Errors;

                    return false;
                }

                password = ParseValue(gitCredentialOutput.Output, "password=");

                bool success = password != null;

                EventMetadata metadata = new EventMetadata
                {
                    { "Success", success },
                    { "CertificatePath", certificatePath }
                };

                if (!success)
                {
                    metadata.Add("Output", gitCredentialOutput.Output);
                }

                activity.Stop(metadata);
                return success;
            }
        }

        public virtual bool TryGetCredential(
            ITracer tracer,
            string repoUrl,
            out string username,
            out string password,
            out string errorMessage)
        {
            username = null;
            password = null;
            errorMessage = null;

            using (ITracer activity = tracer.StartActivity(nameof(this.TryGetCredential), EventLevel.Informational))
            {
                Result gitCredentialOutput = this.InvokeGitAgainstDotGitFolder(
                    GenerateCredentialVerbCommand("fill"),
                    stdin => stdin.Write($"url={repoUrl}\n\n"),
                    parseStdOutLine: null);

                if (gitCredentialOutput.ExitCodeIsFailure)
                {
                    EventMetadata errorData = new EventMetadata();
                    tracer.RelatedWarning(
                        errorData,
                        "Git could not get credentials: " + gitCredentialOutput.Errors,
                        Keywords.Network | Keywords.Telemetry);
                    errorMessage = gitCredentialOutput.Errors;

                    return false;
                }

                username = ParseValue(gitCredentialOutput.Output, "username=");
                password = ParseValue(gitCredentialOutput.Output, "password=");

                bool success = username != null && password != null;

                EventMetadata metadata = new EventMetadata();
                metadata.Add("Success", success);
                if (!success)
                {
                    metadata.Add("Output", gitCredentialOutput.Output);
                }

                activity.Stop(metadata);
                return success;
            }
        }

        public Result DeleteFromLocalConfig(string settingName)
        {
            return this.InvokeGitAgainstDotGitFolder("config --local --unset-all " + settingName);
        }

        public Result SetInLocalConfig(string settingName, string value, bool replaceAll = false, bool add = false)
        {
            return this.InvokeGitAgainstDotGitFolder(string.Format(
                "config --local {0} {1} \"{2}\" \"{3}\"",
                 replaceAll ? "--replace-all " : string.Empty,
                 add ? "--add" : string.Empty,
                 settingName,
                 value));
        }

        public bool TryGetConfigUrlMatch(string section, string repositoryUrl, out Dictionary<string, GitConfigSetting> configSettings)
        {
            Result result = this.InvokeGitAgainstDotGitFolder($"config --get-urlmatch {section} {repositoryUrl}");
            if (result.ExitCodeIsFailure)
            {
                configSettings = null;
                return false;
            }

            configSettings = GitConfigHelper.ParseKeyValues(result.Output, ' ');
            return true;
        }

        public Result TryGetAllConfig(bool localOnly, out Dictionary<string, GitConfigSetting> configSettings)
        {
            configSettings = null;
            string localParameter = localOnly ? "--local" : string.Empty;
            Result result = this.InvokeGitAgainstDotGitFolder("config --list " + localParameter);
            ConfigResult configResult = new ConfigResult(result, "--list");

            if (configResult.TryParseAsString(out string output, out string _, string.Empty))
            {
                configSettings = GitConfigHelper.ParseKeyValues(output);
            }

            return result;
        }

        public Result GetMultiConfig(string settingName)
        {
            string command = $"config --local --get-all {settingName}";
            return this.InvokeGitAgainstDotGitFolder(command);
        }

        /// <summary>
        /// Get the config value give a setting name
        /// </summary>
        /// <param name="settingName">The name of the config setting</param>
        /// <param name="forceOutsideEnlistment">
        /// If false, will run the call from inside the enlistment if the working dir found,
        /// otherwise it will run it from outside the enlistment.
        /// </param>
        /// <returns>The value found for the setting.</returns>
        public ConfigResult GetFromConfig(string settingName, bool forceOutsideEnlistment = false, PhysicalFileSystem fileSystem = null)
        {
            string command = string.Format("config {0}", settingName);
            fileSystem = fileSystem ?? new PhysicalFileSystem();

            // This method is called at clone time, so the physical repo may not exist yet.
            return
                fileSystem.DirectoryExists(this.workingDirectoryRoot) && !forceOutsideEnlistment
                    ? new ConfigResult(this.InvokeGitAgainstDotGitFolder(command), settingName)
                    : new ConfigResult(this.InvokeGitOutsideEnlistment(command), settingName);
        }

        public ConfigResult GetFromLocalConfig(string settingName)
        {
            return new ConfigResult(this.InvokeGitAgainstDotGitFolder("config --local " + settingName), settingName);
        }

        public ConfigResult GetOriginUrl()
        {
            return new ConfigResult(this.InvokeGitAgainstDotGitFolder("config --local remote.origin.url"), "remote.origin.url");
        }

        public Result CreateBranchWithUpstream(string branchToCreate, string upstreamBranch)
        {
            return this.InvokeGitInWorkingDirectoryRoot(
                                "branch " + branchToCreate + " --track " + upstreamBranch,
                                fetchMissingObjects: true);
        }

        public Result ForceCheckout(string target)
        {
            return this.InvokeGitInWorkingDirectoryRoot("checkout -f " + target, fetchMissingObjects: true);
        }

        public Result ForegroundFetch(string remote)
        {
            // By using "--refmap", we override the configured refspec,
            // ignoring the normal "+refs/heads/*:refs/remotes/<remote>/*".
            // The user will see their remote refs update
            // normally when they do a foreground fetch.
            return this.InvokeGitInWorkingDirectoryRoot(
                $"-c credential.interactive=never fetch {remote} --quiet",
                fetchMissingObjects: true,
                userInteractive: false);
        }

        public Result BackgroundFetch(string remote)
        {
            // By using this refspec, we do not create local refs, but instead store them in the "hidden"
            // namespace. These refs are never visible to the user (unless they open the .git/refs dir)
            // but still allow us to run reachability questions like updating the commit-graph.
            string refspec = $"+{ScalarConstants.DotGit.Refs.Heads.RefName}/*"
                           + $":{ScalarConstants.DotGit.Refs.Scalar.Hidden.RefName}/{remote}/*";

            // By using "--refmap", we override the configured refspec,
            // ignoring the normal "+refs/heads/*:refs/remotes/<remote>/*".
            // The user will see their remote refs update
            // normally when they do a foreground fetch.
            return this.InvokeGitInWorkingDirectoryRoot(
                $"-c credential.interactive=never fetch {remote} --quiet --prune --no-tags --refmap= \"{refspec}\"",
                fetchMissingObjects: true,
                userInteractive: false);
        }

        public bool TryGetRemotes(out string[] remotes, out string error)
        {
            GitProcess.Result result = this.InvokeGitInWorkingDirectoryRoot("remote", fetchMissingObjects: false);

            if (result.ExitCodeIsFailure)
            {
                remotes = null;
                error = result.Errors;
                return false;
            }

            remotes = result.Output
                            .Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            error = null;
            return true;
        }

        public Result GvfsHelperDownloadCommit(string commitId)
        {
            return this.InvokeGitInWorkingDirectoryRoot(
                $"gvfs-helper -f post",
                fetchMissingObjects: false,
                writeStdIn: writer =>
                {
                    writer.Write($"{commitId}\n");
                });
        }

        public Result GvfsHelperPrefetch()
        {
            string configString = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "-c http.sslBackend=schannel " : string.Empty;
            return this.InvokeGitInWorkingDirectoryRoot($"{configString}gvfs-helper prefetch", fetchMissingObjects: false);
        }

        public Result PackObjects(string filenamePrefix, string gitObjectsDirectory, Action<StreamWriter> packFileStream)
        {
            string packFilePath = Path.Combine(gitObjectsDirectory, ScalarConstants.DotGit.Objects.Pack.Name, filenamePrefix);

            // Since we don't provide paths we won't be able to complete good deltas
            // avoid the unnecessary computation by setting window/depth to 0
            return this.InvokeGitAgainstDotGitFolder(
                $"pack-objects {packFilePath} --non-empty --window=0 --depth=0 -q",
                packFileStream,
                parseStdOutLine: null,
                gitObjectsDirectory: gitObjectsDirectory);
        }

        /// <summary>
        /// Write a new commit graph in the specified pack directory. Walk starting at refs.
        ///
        /// This will update the graph-head file to point to the new commit graph and delete
        /// any expired graph files that previously existed.
        /// </summary>
        public Result WriteCommitGraph(string objectDir)
        {
            // Do not expire commit-graph files that have been modified in the last hour.
            // This will prevent deleting any commit-graph files that are currently in the commit-graph-chain.
            string command = $"commit-graph write --reachable --split --size-multiple=4 --expire-time={ExpireTimeDateString} --object-dir \"{objectDir}\"";
            return this.InvokeGitInWorkingDirectoryRoot(command, fetchMissingObjects: true);
        }


        public Result VerifyCommitGraph(string objectDir)
        {
            string command = "commit-graph verify --shallow --object-dir \"" + objectDir + "\"";
            return this.InvokeGitInWorkingDirectoryRoot(command, fetchMissingObjects: true);
        }

        public Result IndexPack(string packfilePath, string idxOutputPath)
        {
            return this.InvokeGitAgainstDotGitFolder($"index-pack -o \"{idxOutputPath}\" \"{packfilePath}\"");
        }

        /// <summary>
        /// Write a new multi-pack-index (MIDX) in the specified pack directory.
        ///
        /// If no new packfiles are found, then this is a no-op.
        /// </summary>
        public Result WriteMultiPackIndex(string objectDir)
        {
            // We override the config settings so we keep writing the MIDX file even if it is disabled for reads.
            return this.InvokeGitAgainstDotGitFolder("-c core.multiPackIndex=true multi-pack-index write --object-dir=\"" + objectDir + "\"");
        }

        public Result VerifyMultiPackIndex(string objectDir)
        {
            return this.InvokeGitAgainstDotGitFolder("-c core.multiPackIndex=true multi-pack-index verify --object-dir=\"" + objectDir + "\"");
        }

        public Result RemoteAdd(string remoteName, string url)
        {
            return this.InvokeGitAgainstDotGitFolder("remote add " + remoteName + " " + url);
        }

        public Result PrunePacked(string gitObjectDirectory)
        {
            return this.InvokeGitAgainstDotGitFolder(
                "prune-packed -q",
                writeStdIn: null,
                parseStdOutLine: null,
                gitObjectsDirectory: gitObjectDirectory);
        }

        public Result MultiPackIndexExpire(string gitObjectDirectory)
        {
            return this.InvokeGitAgainstDotGitFolder($"multi-pack-index expire --object-dir=\"{gitObjectDirectory}\"");
        }

        public Result MultiPackIndexRepack(string gitObjectDirectory, string batchSize)
        {
            return this.InvokeGitAgainstDotGitFolder($"-c pack.threads=1 -c repack.packKeptObjects=true multi-pack-index repack --object-dir=\"{gitObjectDirectory}\" --batch-size={batchSize}");
        }

        private Process GetGitProcess(
            string command,
            string workingDirectory,
            string dotGitDirectory,
            bool fetchMissingObjects,
            bool redirectStandardError,
            string gitObjectsDirectory,
            bool userInteractive = true)
        {
            ProcessStartInfo processInfo = new ProcessStartInfo(this.gitBinPath);
            processInfo.WorkingDirectory = workingDirectory;
            processInfo.UseShellExecute = false;
            processInfo.RedirectStandardInput = true;
            processInfo.RedirectStandardOutput = true;
            processInfo.RedirectStandardError = redirectStandardError;
            processInfo.WindowStyle = ProcessWindowStyle.Hidden;
            processInfo.CreateNoWindow = true;

            processInfo.StandardOutputEncoding = UTF8NoBOM;
            processInfo.StandardErrorEncoding = UTF8NoBOM;

            // Removing trace variables that might change git output and break parsing
            // List of environment variables: https://git-scm.com/book/gr/v2/Git-Internals-Environment-Variables
            foreach (string key in processInfo.EnvironmentVariables.Keys.Cast<string>().ToList())
            {
                // If GIT_TRACE is set to a fully-rooted path, then Git sends the trace
                // output to that path instead of stdout (GIT_TRACE=1) or stderr (GIT_TRACE=2).
                if (key.StartsWith("GIT_TRACE", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (!Path.IsPathRooted(processInfo.EnvironmentVariables[key]))
                        {
                            processInfo.EnvironmentVariables.Remove(key);
                        }
                    }
                    catch (ArgumentException)
                    {
                        processInfo.EnvironmentVariables.Remove(key);
                    }
                }
            }

            processInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            processInfo.EnvironmentVariables["GCM_VALIDATE"] = "0";

            if (!userInteractive)
            {
                processInfo.EnvironmentVariables["GCM_INTERACTIVE"] = "Never";
            }

            if (gitObjectsDirectory != null)
            {
                gitObjectsDirectory = Paths.ConvertPathToGitFormat(gitObjectsDirectory);
                processInfo.EnvironmentVariables["GIT_OBJECT_DIRECTORY"] = gitObjectsDirectory;
            }

            if (!fetchMissingObjects)
            {
                command = $"-c {ScalarConstants.GitConfig.UseGvfsHelper}=false {command}";
            }

            if (!string.IsNullOrEmpty(dotGitDirectory))
            {
                command = "--git-dir=\"" + dotGitDirectory + "\" " + command;
            }

            processInfo.Arguments = command;

            Process executingProcess = new Process();
            executingProcess.StartInfo = processInfo;

            return executingProcess;
        }

        protected virtual Result InvokeGitImpl(
            string command,
            string workingDirectory,
            string dotGitDirectory,
            bool fetchMissingObjects,
            Action<StreamWriter> writeStdIn,
            Action<string> parseStdOutLine,
            int timeoutMs,
            string gitObjectsDirectory = null,
            bool userInteractive = true)
        {
            if (failedToSetEncoding && writeStdIn != null)
            {
                return new Result(string.Empty, "Attempting to use to stdin, but the process does not have the right input encodings set.", Result.GenericFailureCode);
            }

            try
            {
                // From https://msdn.microsoft.com/en-us/library/system.diagnostics.process.standardoutput.aspx
                // To avoid deadlocks, use asynchronous read operations on at least one of the streams.
                // Do not perform a synchronous read to the end of both redirected streams.
                using (this.executingProcess = this.GetGitProcess(
                                                        command,
                                                        workingDirectory,
                                                        dotGitDirectory,
                                                        fetchMissingObjects: fetchMissingObjects,
                                                        redirectStandardError: true,
                                                        gitObjectsDirectory: gitObjectsDirectory,
                                                        userInteractive: userInteractive))
                {
                    StringBuilder output = new StringBuilder();
                    StringBuilder errors = new StringBuilder();

                    this.executingProcess.ErrorDataReceived += (sender, args) =>
                    {
                        if (args.Data != null)
                        {
                            errors.Append(args.Data + "\n");
                        }
                    };
                    this.executingProcess.OutputDataReceived += (sender, args) =>
                    {
                        if (args.Data != null)
                        {
                            if (parseStdOutLine != null)
                            {
                                parseStdOutLine(args.Data);
                            }
                            else
                            {
                                output.Append(args.Data + "\n");
                            }
                        }
                    };

                    lock (this.executionLock)
                    {
                        lock (this.processLock)
                        {
                            if (this.stopping)
                            {
                                return new Result(string.Empty, nameof(GitProcess) + " is stopping", Result.GenericFailureCode);
                            }

                            this.executingProcess.Start();

                            this.executingProcess.BeginOutputReadLine();
                            this.executingProcess.BeginErrorReadLine();

                            try
                            {
                                if (this.LowerPriority)
                                {
                                    this.executingProcess.PriorityClass = ProcessPriorityClass.BelowNormal;
                                }

                                if (writeStdIn != null)
                                {
                                    writeStdIn.Invoke(this.executingProcess.StandardInput);
                                    this.executingProcess.StandardInput.Close();
                                }
                            }
                            catch (InvalidOperationException)
                            {
                                // This is thrown if the process completes before we can set a property.
                            }
                            catch (Win32Exception)
                            {
                                // This is thrown if the process completes before we can set a property.
                            }

                            if (!this.executingProcess.WaitForExit(timeoutMs))
                            {
                                this.executingProcess.Kill();

                                return new Result(output.ToString(), "Operation timed out: " + errors.ToString(), Result.GenericFailureCode);
                            }
                        }

                        return new Result(output.ToString(), errors.ToString(), this.executingProcess.ExitCode);
                    }
                }
            }
            catch (Win32Exception e)
            {
                return new Result(string.Empty, e.Message, Result.GenericFailureCode);
            }
            finally
            {
                this.executingProcess = null;
            }
        }

        private static string GenerateCredentialVerbCommand(string verb)
        {
            return $"-c {GitConfigSetting.CredentialUseHttpPath}=true credential {verb}";
        }

        private static string ParseValue(string contents, string prefix)
        {
            int startIndex = contents.IndexOf(prefix) + prefix.Length;
            if (startIndex >= 0 && startIndex < contents.Length)
            {
                int endIndex = contents.IndexOf('\n', startIndex);
                if (endIndex >= 0 && endIndex < contents.Length)
                {
                    return
                        contents
                        .Substring(startIndex, endIndex - startIndex)
                        .Trim('\r');
                }
            }

            return null;
        }

        /// <summary>
        /// Invokes git.exe without a working directory set.
        /// </summary>
        /// <remarks>
        /// For commands where git doesn't need to be (or can't be) run from inside an enlistment.
        /// eg. 'git init' or 'git version'
        /// </remarks>
        private Result InvokeGitOutsideEnlistment(string command)
        {
            return this.InvokeGitOutsideEnlistment(command, null, null);
        }

        private Result InvokeGitOutsideEnlistment(
            string command,
            Action<StreamWriter> writeStdIn,
            Action<string> parseStdOutLine,
            int timeout = -1)
        {
            return this.InvokeGitImpl(
                command,
                workingDirectory: Environment.SystemDirectory,
                dotGitDirectory: null,
                fetchMissingObjects: false,
                writeStdIn: writeStdIn,
                parseStdOutLine: parseStdOutLine,
                timeoutMs: timeout);
        }

        /// <summary>
        /// Invokes git.exe from an enlistment's repository root
        /// </summary>
        private Result InvokeGitInWorkingDirectoryRoot(
            string command,
            bool fetchMissingObjects,
            Action<StreamWriter> writeStdIn = null,
            Action<string> parseStdOutLine = null,
            bool userInteractive = true)
        {
            return this.InvokeGitImpl(
                command,
                workingDirectory: this.workingDirectoryRoot,
                dotGitDirectory: null,
                fetchMissingObjects: fetchMissingObjects,
                writeStdIn: writeStdIn,
                parseStdOutLine: parseStdOutLine,
                timeoutMs: -1,
                userInteractive: userInteractive);
        }

        /// <summary>
        /// Invokes git.exe against an enlistment's .git folder.
        /// This method should be used only with git-commands that ignore the working directory
        /// </summary>
        private Result InvokeGitAgainstDotGitFolder(string command)
        {
            return this.InvokeGitAgainstDotGitFolder(command, null, null);
        }

        private Result InvokeGitAgainstDotGitFolder(
            string command,
            Action<StreamWriter> writeStdIn,
            Action<string> parseStdOutLine,
            string gitObjectsDirectory = null)
        {
            // This git command should not need/use the working directory of the repo.
            // Run git.exe in Environment.SystemDirectory to ensure the git.exe process
            // does not touch the working directory
            return this.InvokeGitImpl(
                command,
                workingDirectory: Environment.SystemDirectory,
                dotGitDirectory: this.dotGitRoot,
                fetchMissingObjects: false,
                writeStdIn: writeStdIn,
                parseStdOutLine: parseStdOutLine,
                timeoutMs: -1,
                gitObjectsDirectory: gitObjectsDirectory);
        }

        public class Result
        {
            public const int SuccessCode = 0;
            public const int GenericFailureCode = 1;

            public Result(string stdout, string stderr, int exitCode)
            {
                this.Output = stdout;
                this.Errors = stderr;
                this.ExitCode = exitCode;
            }

            public string Output { get; }
            public string Errors { get; }
            public int ExitCode { get; }

            public bool ExitCodeIsSuccess
            {
                get { return this.ExitCode == Result.SuccessCode; }
            }

            public bool ExitCodeIsFailure
            {
                get { return !this.ExitCodeIsSuccess; }
            }

            public bool StderrContainsErrors()
            {
                if (!string.IsNullOrWhiteSpace(this.Errors))
                {
                    return !this.Errors
                        .Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .All(line => line.TrimStart().StartsWith("warning:", StringComparison.OrdinalIgnoreCase));
                }

                return false;
            }
        }

        public class ConfigResult
        {
            private readonly Result result;
            private readonly string configName;

            public ConfigResult(Result result, string configName)
            {
                this.result = result;
                this.configName = configName;
            }

            public bool TryParseAsString(out string value, out string error, string defaultValue = null)
            {
                value = defaultValue;
                error = string.Empty;

                if (this.result.ExitCodeIsFailure && this.result.StderrContainsErrors())
                {
                    error = "Error while reading '" + this.configName + "' from config: " + this.result.Errors;
                    return false;
                }

                if (this.result.ExitCodeIsSuccess)
                {
                    value = this.result.Output?.TrimEnd('\n');
                }

                return true;
            }

            public bool TryParseAsInt(int defaultValue, int minValue, out int value, out string error)
            {
                value = defaultValue;
                error = string.Empty;

                if (!this.TryParseAsString(out string valueString, out error))
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(valueString))
                {
                    // Use default value
                    return true;
                }

                if (!int.TryParse(valueString, out value))
                {
                    error = string.Format("Misconfigured config setting {0}, could not parse value `{1}` as an int", this.configName, valueString);
                    return false;
                }

                if (value < minValue)
                {
                    error = string.Format("Invalid value {0} for setting {1}, value must be greater than or equal to {2}", value, this.configName, minValue);
                    return false;
                }

                return true;
            }
        }

        public class MultiConfigResult
        {
            public Result Result { get; }
            public HashSet<string> Values { get; }

            public MultiConfigResult(Result result)
            {
                this.Result = result;
                this.Values = new HashSet<string>(this.Result.Output.Split("\n", StringSplitOptions.RemoveEmptyEntries));
            }
        }
    }
}
