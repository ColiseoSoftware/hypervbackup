/*
 *  Copyright 2012 Cloudbase Solutions Srl + 2015 Coliseo Software srl
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
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using CommandLine;
using CommandLine.Text;
using HyperVBackUp.Engine;

namespace HyperVBackup.Console
{
    class Program
    {
        static volatile bool cancel = false;
        static int currentWidth = 0;
        static int consoleWidth = 0;

        class Options
        {
            [Option('f', "file", HelpText = "Text file containing a list of VMs to backup, one per line.", MutuallyExclusiveSet = "fla")]
            public string File { get; set; }

            [OptionList('l', "list", Separator = ',', HelpText = "List of VMs to backup, comma separated.", MutuallyExclusiveSet = "fla")]
            public IList<string> List { get; set; }

            [OptionList('v', "vhdinclude", Separator = ',', HelpText = "List of VHDs file names to backup, comma separated.")]
            public IList<string> VhdInclude { get; set; }

            [OptionList('i', "vhdignore", Separator = ',', HelpText = "List of VHDs file names to ignore, comma separated.")]
            public IList<string> VhdIgnore { get; set; }

            [Option('a', "all", HelpText = "Is set, backup all VMs on this server.", MutuallyExclusiveSet = "fla", DefaultValue = true)]
            public bool All { get; set; }

            [Option('n', "name", HelpText = "If set, VMs to backup are specified by name.", MutuallyExclusiveSet = "ng", DefaultValue = true)]
            public bool Name { get; set; }

            [Option('g', "guid", HelpText = "If set, VMs to backup are specified by guid.", MutuallyExclusiveSet = "ng")]
            public bool Guid { get; set; }

            [Option('o', "output", Required = true, HelpText = "Backup ouput folder.")]
            public string Output { get; set; }

            [Option('p', "password", HelpText = "Secure the backup with a password.")]
            public string Password { get; set; }

            private string outputFormat = "{0}_{2:yyyyMMddHHmmss}.{3}";
            [Option("outputformat", HelpText = "Backup archive name format. {0} is the VM's name, {1} the VM's GUID and {2} is the current date and time. Default: \"{0}_{2:yyyyMMddHHmmss}.zip\"")]
            public string OutputFormat
            {
                get
                {
                    return outputFormat;
                }
                set
                {
                    outputFormat = value;
                }
            }

            [Option('s', "singlevss", HelpText = "Perform one single snapshot for all the VMs.")]
            public bool SingleSnapshot { get; set; }

            [Option("compressionlevel", DefaultValue = 6, HelpText = "Compression level, between 0 (no compression) and 9 (max. compression).")]
            public int CompressionLevel { get; set; }

            [Option("cleanoutputbydays", DefaultValue = 0, HelpText = "Delete all files in the output folder older than x days. TOTALLY OPTIONAL. USE WITH CAUTION.")]
            public int CleanOutputDays { get; set; }

            [Option("cleanoutputbymb", DefaultValue = 0, HelpText = "Delete older files in the output folder if total size is bigger then x Megabytes. TOTALLY OPTIONAL. USE WITH CAUTION.")]
            public int CleanOutputMb { get; set; }

            [ParserState]
            public IParserState LastParserState { get; set; }

            [HelpOption(HelpText = "Display this help screen.")]
            public string GetUsage()
            {
                var header = new StringBuilder();
                header.AppendLine();
                header.AppendLine("Note: short switchs use one dash (-) / long switchs use two dashes (--). Example: HyperVBackup -l \"Mail Server\" --compressionlevel 0");
                header.AppendLine();

                var help = new HelpText(header.ToString()) { AdditionalNewLineAfterOption = false };
                HandleParsingErrorsInHelp(help);
                help.AddOptions(this);

                return help.ToString();
            }

            private void HandleParsingErrorsInHelp(HelpText help)
            {
                string errors = help.RenderParsingErrorsText(this, 1);
                if (!string.IsNullOrEmpty(errors))
                    help.AddPreOptionsLine(string.Concat("ERROR: ", errors, Environment.NewLine));
            }
        }

        static void Main(string[] args)
        {
            try
            {
                Stopwatch stopwatch = new Stopwatch();
                stopwatch.Start();

                System.Console.WriteLine("HyperVBackup 2.1");
                System.Console.WriteLine("Copyright (C) 2012 Cloudbase Solutions Srl");
                System.Console.WriteLine("Copyright (C) 2015 Coliseo Software Srl");

                var parser = new Parser(ConfigureSettings);
                var options = new Options();
                if (parser.ParseArgumentsStrict(args, options, () => Environment.Exit(1)))
                {
                    GetConsoleWidth();
                    System.Console.WriteLine();

                    if (options.CleanOutputDays != 0)
                        CleanOutputByDays(options.Output, options.CleanOutputDays);

                    if (options.CleanOutputMb != 0)
                        CleanOutputByMegabytes(options.Output, options.CleanOutputMb);

                    var vmNames = GetVMNames(options);

                    if (vmNames == null)
                        System.Console.WriteLine("Backing up all VMs on this server");

                    if (!Directory.Exists(options.Output))
                        throw new Exception(string.Format("The folder \"{0}\" is not valid", options.Output));

                    if (options.CleanOutputDays != 0)
                        CleanOutputByDays(options.Output, options.CleanOutputDays);

                    if (options.CleanOutputMb != 0)
                        CleanOutputByMegabytes(options.Output, options.CleanOutputMb);

                    VMNameType nameType = options.Name ? VMNameType.ElementName : VMNameType.SystemName;

                    BackupManager mgr = new BackupManager();
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
                        Password = options.Password
                    };

                    var vmNamesMap = mgr.VSSBackup(vmNames, nameType, backupOptions);

                    CheckRequiredVMs(vmNames, nameType, vmNamesMap);

                    ShowElapsedTime(stopwatch);
                }
            }
            catch (BackupCancelledException ex)
            {
                System.Console.Error.WriteLine(string.Format(ex.Message));
                Environment.Exit(3);
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine(string.Format("Error: {0}", ex.Message));
                System.Console.Error.WriteLine(ex.StackTrace);
                Environment.Exit(2);
            }

            Environment.Exit(cancel ? 3 : 0);
        }



        private static void CleanOutputByDays(string output, int days)
        {
            var files = Directory.GetFiles(output);

            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTime < DateTime.Now.AddDays(days * -1))
                {
                    System.Console.WriteLine("Deleting file {0}", fileInfo.Name);
                    fileInfo.Delete();
                }
            }
        }


        private static void CleanOutputByMegabytes(string output, int totalMb)
        {
            var dirInfo = new DirectoryInfo(output);
            var totalSize = dirInfo.EnumerateFiles().Sum(file => file.Length);
            long desiredMaxSize = (long)totalMb * 1024 * 1024;

            if (totalSize > desiredMaxSize)
            {
                System.Console.WriteLine("Size of output folder is {0} Megabytes, deleting some files ...", totalSize / 1024 / 1024);

                var files = dirInfo.GetFiles();
                while (totalSize > desiredMaxSize && files.Length != 0)
                {
                    var sortedFiles = files.OrderBy(f => f.LastWriteTime).ToList();
                    var filetoDelete = sortedFiles[0].FullName;

                    System.Console.WriteLine("Deleting file {0}", filetoDelete);
                    File.Delete(filetoDelete);

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
            System.Console.WriteLine();
            System.Console.WriteLine(string.Format("Elapsed time: {0:00}:{1:00}:{2:00}.{3:000}", ts.Hours, ts.Minutes, ts.Seconds, ts.Milliseconds));
        }

        private static void CheckRequiredVMs(IEnumerable<string> vmNames, VMNameType nameType, IDictionary<string, string> vmNamesMap)
        {
            if (vmNames != null)
                foreach (var vmName in vmNames)
                    if (nameType == VMNameType.SystemName && !vmNamesMap.Keys.Contains(vmName, StringComparer.OrdinalIgnoreCase) ||
                       nameType == VMNameType.ElementName && !vmNamesMap.Values.Contains(vmName, StringComparer.OrdinalIgnoreCase))
                    {
                        System.Console.WriteLine(string.Format("WARNING: \"{0}\" not found", vmName));
                    }
        }

        private static void GetConsoleWidth()
        {
            try
            {
                consoleWidth = System.Console.WindowWidth;
            }
            catch (Exception)
            {
                consoleWidth = 80;
            }
        }

        private static IEnumerable<string> GetVMNames(Options options)
        {
            IEnumerable<string> vmNames = null;

            if (options.File != null)
                vmNames = File.ReadAllLines(options.File);
            else if (options.List != null)
                vmNames = options.List;

            if (vmNames != null)
                vmNames = (from o in vmNames where o.Trim().Length > 0 select o.Trim());

            return vmNames;
        }

        static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            System.Console.Error.WriteLine();
            System.Console.Error.WriteLine("Cancelling backup...");

            // Avoid CTRL+C during VSS snapshots
            cancel = true;
            e.Cancel = true;
        }

        static void MgrBackupProgress(object sender, BackupProgressEventArgs e)
        {
            switch (e.Action)
            {
                case EventAction.InitializingVSS:
                    System.Console.WriteLine("Initializing VSS");
                    break;
                case EventAction.StartingSnaphotSet:
                    System.Console.WriteLine();
                    System.Console.WriteLine("Starting snapshot set for:");
                    foreach (var componentName in e.Components.Values)
                        System.Console.WriteLine(componentName);
                    System.Console.WriteLine();
                    System.Console.WriteLine("Volumes:");
                    foreach (var volumePath in e.VolumeMap.Keys)
                        System.Console.WriteLine(volumePath);
                    break;
                case EventAction.DeletingSnapshotSet:
                    System.Console.WriteLine("Deleting snapshot set");
                    break;
                case EventAction.StartingArchive:
                    System.Console.WriteLine();
                    foreach (var componentName in e.Components.Values)
                        System.Console.WriteLine(string.Format("Component: \"{0}\"", componentName));
                    System.Console.WriteLine(string.Format("Archive: \"{0}\"", e.AcrhiveFileName));
                    break;
                case EventAction.StartingEntry:
                    System.Console.WriteLine(string.Format("Entry: \"{0}\"", e.CurrentEntry));
                    currentWidth = 0;
                    break;
                case EventAction.SavingEntry:
                    if (e.TotalBytesToTransfer > 0)
                    {
                        int width = (int)Math.Round(e.BytesTransferred * consoleWidth / (decimal)e.TotalBytesToTransfer);

                        for (int i = 0; i < width - currentWidth; i++)
                            System.Console.Write(".");
                        currentWidth = width;

                        if (e.BytesTransferred == e.TotalBytesToTransfer)
                            System.Console.WriteLine();

                        //Console.WriteLine(string.Format("{0:0.#}%", e.BytesTransferred * 100 / (decimal)e.TotalBytesToTransfer));
                    }
                    break;
                case EventAction.PercentProgress:
                    int progressWidth = e.PercentDone * consoleWidth / 100;

                    for (int i = 0; i < progressWidth - currentWidth; i++)
                        System.Console.Write(".");
                    currentWidth = progressWidth;

                    if (e.PercentDone == 100)
                        System.Console.WriteLine();

                    break;
            }

            e.Cancel = cancel;
        }
    }
}
