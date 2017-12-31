/*
 *  Copyright 2012 Cloudbase Solutions Srl + 2016 Coliseo Software srl
 *
 *  This program is free software; you can redistribute it and/or modify
 *  it under the terms of the GNU Lesser General Public License as published by
 *  the Free Software Foundation; either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU Lesser General Public License for more details.
 *
 *  You should have received a copy of the GNU Lesser General Public License
 *  along with this program; if not, write to the Free Software
 *  Foundation, Inc., 51 Franklin St, Fifth Floor, Boston, MA  02110-1301  USA 
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using CommandLine;
using CommandLine.Text;
using HyperVBackUp.Engine;
using NLog;

namespace HyperVBackup.Console
{
    class Program
    {
        static volatile bool _cancel = false;
        static int _currentWidth = 0;
        static int _consoleWidth = 0;
        private static ILogger _logger;

        class Options
        {
            [Option('f', "file", HelpText = "Text file containing a list of VMs to backup, one per line. If not set all VM are included.", MutuallyExclusiveSet = "flax")]
            public string File { get; set; }

            [OptionList('l', "list", Separator = ',', HelpText = "List of VMs to backup, comma separated. If not set all VM are included.", MutuallyExclusiveSet = "flax")]
            public IList<string> List { get; set; }

            [OptionList('x', "exclude", Separator = ',', HelpText = "List of VMs to exclude from backup, comma separated. Use when all VM are included.", MutuallyExclusiveSet = "flax")]
            public IList<string> Exclude { get; set; }

            [OptionList('v', "vhdinclude", Separator = ',', HelpText = "List of VHDs file names to backup, comma separated.")]
            public IList<string> VhdInclude { get; set; }

            [OptionList('i', "vhdignore", Separator = ',', HelpText = "List of VHDs file names to ignore, comma separated.")]
            public IList<string> VhdIgnore { get; set; }

            [Option('n', "name", HelpText = "If set, VMs to backup are specified by name.", MutuallyExclusiveSet = "ng", DefaultValue = true)]
            public bool Name { get; set; }

            [Option('o', "output", Required = true, HelpText = "Backup ouput folder.")]
            public string Output { get; set; }

            [Option('p', "password", HelpText = "Secure the backup with a password.")]
            public string Password { get; set; }

            [Option('z', "zip", HelpText = "Use the zip format to store the backup insted of the 7zip format.", MutuallyExclusiveSet = "zd")]
            public bool ZipFormat { get; set; }

            [Option('d', "directcopy", HelpText = "Do not compress the output, just copy the files recreating the folder structure.", MutuallyExclusiveSet = "zd")]
            public bool DirectCopy { get; set; }

            [Option("outputformat", HelpText = "Backup archive name format. {0} is the VM's name, {1} the VM's GUID, {2} is the current date and time and {3} is the extension for the compression format (7z or zip). Default: \"{0}_{2:yyyyMMddHHmmss}{3}\"")]
            public string OutputFormat { get; set; } = "{0}_{2:yyyyMMddHHmmss}{3}";

            [Option('s', "singlevss", HelpText = "Perform one single snapshot for all the VMs.")]
            public bool SingleSnapshot { get; set; }

            [Option("compressionlevel", DefaultValue = -1, HelpText = "Compression level, between 0 (no compression, very fast) and 9 (max. compression, very slow).")]
            public int CompressionLevel { get; set; }

            [Option("cleanoutputbydays", DefaultValue = 0, HelpText = "Delete all files in the output folder older than x days. TOTALLY OPTIONAL. USE WITH CAUTION.")]
            public int CleanOutputDays { get; set; }

            [Option("cleanoutputbymb", DefaultValue = 0, HelpText = "Delete older files in the output folder if total size is bigger then x Megabytes. TOTALLY OPTIONAL. USE WITH CAUTION.")]
            public int CleanOutputMb { get; set; }

            [Option("onsuccess", HelpText = "Execute this program if backup completes correctly.")]
            public string OnSuccess { get; set; }

            [Option("onfailure", HelpText = "Execute this program if backup fails.")]
            public string OnFailure { get; set; }

            [Option("mt", HelpText = "Enable multi-threaded compression (only for 7zip format).")]
            public bool MultiThreaded { get; set; }

            [HelpOption(HelpText = "Display this help screen.")]
            public string GetUsage()
            {
                var header = new StringBuilder();
                header.AppendLine();
                header.AppendLine("Note: short switchs use one dash (-) / long switches use two dashes (--). Example: HyperVBackup -l \"Mail Server\" --compressionlevel 0");
                header.AppendLine();

                var help = new HelpText(header.ToString()) { AdditionalNewLineAfterOption = false };
                HandleParsingErrorsInHelp(help);
                help.AddOptions(this);

                return help.ToString();
            }

            private void HandleParsingErrorsInHelp(HelpText help)
            {
                var errors = help.RenderParsingErrorsText(this, 1);
                if (!string.IsNullOrEmpty(errors))
                {
                    help.AddPreOptionsLine(string.Concat("ERROR: ", errors, Environment.NewLine));
                }
            }
        }

        private static void Main(string[] args)
        {
            _logger = LogManager.GetCurrentClassLogger();
            var options = new Options();

            try
            {
                var stopwatch = new Stopwatch();
                stopwatch.Start();

                System.Console.WriteLine("HyperVBackup 3");
                System.Console.WriteLine("Copyright (C) 2012 Cloudbase Solutions SRL");
                System.Console.WriteLine("Copyright (C) 2016/2017 Coliseo Software SRL");

                _logger.Info($"HyperVBackup started at {DateTime.Now}");

                var parser = new Parser(ConfigureSettings);
                if (parser.ParseArgumentsStrict(args, options, () => Environment.Exit(1)))
                {
                    GetConsoleWidth();
                    System.Console.WriteLine();

                    var vmNames = GetVmNames(options);

                    if (vmNames == null)
                    {
                        _logger.Info("Backing up all VMs on this server");
                    }

                    if (!Directory.Exists(options.Output))
                    {
                        throw new Exception($"The folder \"{options.Output}\" is not valid");
                    }

                    if (options.CleanOutputDays != 0)
                    {
                        CleanOutputByDays(options.Output, options.CleanOutputDays);
                    }

                    if (options.CleanOutputMb != 0)
                    {
                        CleanOutputByMegabytes(options.Output, options.CleanOutputMb);
                    }

                    var nameType = options.Name ? VmNameType.ElementName : VmNameType.SystemName;

                    var mgr = new BackupManager();
                    mgr.BackupProgress += MgrBackupProgress;

                    System.Console.CancelKeyPress += Console_CancelKeyPress;

                    var backupOptions = new HyperVBackUp.Engine.Options
                    {
                        CompressionLevel = options.CompressionLevel,
                        Output = options.Output,
                        OutputFormat = options.OutputFormat,
                        SingleSnapshot = options.SingleSnapshot,
                        VhdInclude = options.VhdInclude,
                        VhdIgnore = options.VhdIgnore,
                        Password = options.Password,
                        ZipFormat = options.ZipFormat,
                        DirectCopy = options.DirectCopy,
                        MultiThreaded = options.MultiThreaded,
                        Exclude = options.Exclude
                    };

                    var vmNamesMap = mgr.VssBackup(vmNames, nameType, backupOptions, _logger);

                    CheckRequiredVMs(vmNames, nameType, vmNamesMap);

                    ShowElapsedTime(stopwatch);

                    if (!string.IsNullOrEmpty(options.OnSuccess))
                    {
                        _logger.Info("Executing OnSucess program");
                        ExecuteProcess(options.OnSuccess, _logger);

                    }

                    _logger.Info($"HyperVBackup ended at {DateTime.Now}");
                }
            }
            catch (BackupCancelledException ex)
            {
                System.Console.Error.WriteLine(string.Format(ex.Message));
                _logger.Error(ex.ToString());

                if (!string.IsNullOrEmpty(options.OnFailure))
                {
                    _logger.Info("Executing OnFailure program");
                    ExecuteProcess(options.OnFailure, _logger);
                }

                Environment.Exit(3);
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"Error: {ex.Message}");
                System.Console.Error.WriteLine(ex.StackTrace);
                _logger.Error(ex.ToString());

                if (!string.IsNullOrEmpty(options.OnFailure))
                {
                    _logger.Info("Executing OnFailure program");
                    ExecuteProcess(options.OnFailure, _logger);
                }

                Environment.Exit(2);
            }

            Environment.Exit(_cancel ? 3 : 0);
        }

        private static void ExecuteProcess(string fileName, ILogger logger)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return;
            }

            using (var process = new Process())
            {
                logger.Debug($"Executing program {fileName}");

                process.StartInfo.FileName = fileName;
                process.StartInfo.WorkingDirectory = Path.GetDirectoryName(fileName);
                process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;

                process.Start();
                process.WaitForExit();

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                var exitCode = process.ExitCode;

                process.Close();

                logger.Info($"Exit code of executing program {fileName} is {exitCode}");
                logger.Debug($"Output of executing program {fileName} is {output}");
                logger.Debug($"Errors of executing program {fileName} are {error}");
            }
        }

        private static void CleanOutputByDays(string output, int days)
        {
            foreach (var file in Directory.GetFiles(output))
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTime < DateTime.Now.AddDays(days * -1))
                {
                    _logger.Info($"Deleting file {fileInfo.Name}");
                    fileInfo.Delete();
                }
            }
        }

        private static void CleanOutputByMegabytes(string output, int totalMb)
        {
            var dirInfo = new DirectoryInfo(output);
            var totalSize = dirInfo.EnumerateFiles().Sum(file => file.Length);
            var desiredMaxSize = (long)totalMb * 1024 * 1024;

            if (totalSize > desiredMaxSize)
            {
                _logger.Info($"Size of output folder is {totalSize / 1024 / 1024} Megabytes, deleting some files ...");

                var files = dirInfo.GetFiles();
                while (totalSize > desiredMaxSize && files.Length != 0)
                {
                    var sortedFiles = files.OrderBy(f => f.LastWriteTime).ToList();
                    var filetoDelete = sortedFiles[0].FullName;
                    _logger.Info($"Deleting file {filetoDelete}");

                    try
                    {
                        File.Delete(filetoDelete);
                    }
                    catch (Exception e)
                    {
                        _logger.Error($"Cannot delete file {filetoDelete} - {e}");
                    }

                    files = dirInfo.GetFiles();
                    totalSize = files.Sum(file => file.Length);
                }
            }
        }

        private static void ConfigureSettings(ParserSettings settings)
        {
            settings.MutuallyExclusive = true;
            settings.HelpWriter = System.Console.Out;
        }

        private static void ShowElapsedTime(Stopwatch stopwatch)
        {
            stopwatch.Stop();
            var ts = stopwatch.Elapsed;
            _logger.Info(
                $"Elapsed time: {ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}");
        }

        private static void CheckRequiredVMs(IEnumerable<string> vmNames, VmNameType nameType, IDictionary<string, string> vmNamesMap)
        {
            if (vmNames != null)
                foreach (var vmName in vmNames)
                    if (nameType == VmNameType.SystemName && !vmNamesMap.Keys.Contains(vmName, StringComparer.OrdinalIgnoreCase) ||
                       nameType == VmNameType.ElementName && !vmNamesMap.Values.Contains(vmName, StringComparer.OrdinalIgnoreCase))
                    {
                        _logger.Error($"WARNING: \"{vmName}\" not found");
                    }
        }

        private static void GetConsoleWidth()
        {
            try
            {
                _consoleWidth = System.Console.WindowWidth;
            }
            catch (Exception)
            {
                _consoleWidth = 80;
            }
        }

        private static ICollection<string> GetVmNames(Options options)
        {
            ICollection<string> vmNames = null;

            if (options.File != null)
            {
                vmNames = File.ReadAllLines(options.File);
            }
            else if (options.List != null)
            {
                vmNames = options.List;
            }

            if (vmNames != null)
            {
                vmNames = (from o in vmNames where o.Trim().Length > 0 select o.Trim()).ToList();
            }

            return vmNames;
        }

        static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            System.Console.Error.WriteLine();
            System.Console.Error.WriteLine("Cancelling backup...");

            // Avoid CTRL+C during VSS snapshots
            _cancel = true;
            e.Cancel = true;
        }

        static void MgrBackupProgress(object sender, BackupProgressEventArgs e)
        {
            switch (e.Action)
            {
                case EventAction.InitializingVss:
                    _logger.Info("Initializing VSS");
                    break;

                case EventAction.StartingSnaphotSet:
                    _logger.Info("Starting snapshot set for:");

                    foreach (var componentName in e.Components.Values)
                    {
                        _logger.Info(componentName);
                    }

                    _logger.Info("");
                    _logger.Info("Volumes:");
                    foreach (var volumePath in e.VolumeMap.Keys)
                    {
                        System.Console.WriteLine(volumePath);
                    }
                    break;

                case EventAction.DeletingSnapshotSet:
                    _logger.Info("Deleting snapshot set");
                    break;

                case EventAction.StartingArchive:
                    _logger.Info("");
                    _logger.Info($"Creating archive: \"{e.AcrhiveFileName}\"");
                    break;

                case EventAction.StartingEntry:
                    _logger.Info($"Compressing entry: \"{e.CurrentEntry}\"");
                    _currentWidth = 0;
                    break;

                //case EventAction.SavingEntry:
                //    if (e.TotalBytesToTransfer > 0)
                //    {
                //        var width = (int)Math.Round(e.BytesTransferred * _consoleWidth / (decimal)e.TotalBytesToTransfer);

                //        for (var i = 0; i < width - _currentWidth; i++)
                //        {
                //            System.Console.Write(".");
                //        }

                //        _currentWidth = width;

                //        if (e.BytesTransferred == e.TotalBytesToTransfer)
                //        {
                //            System.Console.WriteLine();
                //        }

                //        System.Console.WriteLine($"{e.BytesTransferred * 100 / (decimal) e.TotalBytesToTransfer:0.#}%");
                //    }
                //    break;

                case EventAction.PercentProgress:
                    var progressWidth = e.PercentDone * _consoleWidth / 100;

                    for (var i = 0; i < progressWidth - _currentWidth; i++)
                    {
                        System.Console.Write(".");
                    }
                    _currentWidth = progressWidth;

                    if (e.PercentDone == 100)
                    {
                        System.Console.WriteLine();
                    }

                    _logger.Debug($"Percent done: {e.PercentDone}%");

                    break;
            }

            e.Cancel = _cancel;
        }
    }
}
