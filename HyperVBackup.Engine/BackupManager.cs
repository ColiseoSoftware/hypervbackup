/*
 *  Copyright 2012 Cloudbase Solutions Srl + Coliseo Software Srl
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
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using Alphaleonis.Win32.Vss;
using SevenZip;
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using File = Alphaleonis.Win32.Filesystem.File;
using Path = Alphaleonis.Win32.Filesystem.Path;

namespace HyperVBackUp.Engine
{
    public class BackupCancelledException : Exception
    {
        public BackupCancelledException()
            : base("Backup cancelled")
        {
        }
    }

    public enum EventAction
    {
        InitializingVss,
        StartingSnaphotSet,
        SnapshotSetDone,
        StartingArchive,
        StartingEntry,
        SavingEntry,
        ArchiveDone,
        PercentProgress,
        DeletingSnapshotSet
    }

    public class BackupProgressEventArgs : EventArgs
    {
        public IDictionary<string, string> Components { get; set; }
        public string AcrhiveFileName { get; set; }
        public long BytesTransferred { get; set; }
        public bool Cancel { get; set; }
        public string CurrentEntry { get; set; }
        public int EntriesTotal { get; set; }
        public int PercentDone { get; set; }
        public long TotalBytesToTransfer { get; set; }
        public EventAction Action { get; set; }
        public IDictionary<string, string> VolumeMap { get; set; }
    }

    public enum VmNameType { ElementName, SystemName }

    public class BackupManager
    {
        public event EventHandler<BackupProgressEventArgs> BackupProgress;
        private volatile bool _cancel = false;

        public IDictionary<string, string> VssBackup(IEnumerable<string> vmNames, VmNameType nameType, Options options)
        {
            _cancel = false;
            var vmNamesMap = GetVMNames(vmNames, nameType);

            if (vmNamesMap.Count > 0)
            {
                if (options.SingleSnapshot)
                    BackupSubset(vmNamesMap, options);
                else
                    foreach (var kv in vmNamesMap)
                    {
                        var vmNamesMapSubset = new Dictionary<string, string> { { kv.Key, kv.Value } };
                        BackupSubset(vmNamesMapSubset, options);
                    }
            }

            return vmNamesMap;
        }

        private void BackupSubset(IDictionary<string, string> vmNamesMapSubset, Options options)
        {
            var vssImpl = VssUtils.LoadImplementation();
            using (var vss = vssImpl.CreateVssBackupComponents())
            {
                RaiseEvent(EventAction.InitializingVss, null, null);

                vss.InitializeForBackup(null);
                vss.SetBackupState(true, true, VssBackupType.Full, false);
                vss.SetContext(VssSnapshotContext.Backup);

                // Add Hyper-V writer
                var hyperVwriterGuid = new Guid("66841cd4-6ded-4f4b-8f17-fd23f8ddc3de");
                vss.EnableWriterClasses(new Guid[] { hyperVwriterGuid });

                vss.GatherWriterMetadata();

                IList<IVssWMComponent> components = new List<IVssWMComponent>();
                // key: volumePath, value: volumeName. These values are equivalent on a standard volume, but differ in the CSV case  
                IDictionary<string, string> volumeMap = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

                var wm = vss.WriterMetadata.FirstOrDefault(o => o.WriterId.Equals(hyperVwriterGuid));
                foreach (var component in wm.Components)
                {
                    if (vmNamesMapSubset.ContainsKey(component.ComponentName))
                    {
                        components.Add(component);
                        vss.AddComponent(wm.InstanceId, wm.WriterId, component.Type, component.LogicalPath, component.ComponentName);
                        foreach (var file in component.Files)
                        {
                            string volumeName = null;
                            string volumePath = null;

                            if (CSV.IsSupported() && CSV.IsPathOnSharedVolume(file.Path))
                            {
                                CSV.ClusterPrepareSharedVolumeForBackup(file.Path, out volumePath, out volumeName);
                            }
                            else
                            {
                                volumePath = Path.GetPathRoot(file.Path).ToUpper();
                                volumeName = volumePath;
                            }

                            if (!volumeMap.ContainsKey(volumePath)) // added StringComparer.InvariantCultureIgnoreCase to volumeMap Dictionary to fix duplicate Keys with different case
                                volumeMap.Add(volumePath, volumeName);
                        }
                    }
                }

                if (components.Count > 0)
                {
                    var vssSet = vss.StartSnapshotSet();

                    // Key: volumeName, value: snapshotGuid
                    IDictionary<string, Guid> snapshots = new Dictionary<string, Guid>();

                    foreach (var volumeName in volumeMap.Values)
                        snapshots.Add(volumeName, vss.AddToSnapshotSet(volumeName, Guid.Empty));

                    vss.PrepareForBackup();

                    RaiseEvent(EventAction.StartingSnaphotSet, components, volumeMap);
                    vss.DoSnapshotSet();
                    RaiseEvent(EventAction.SnapshotSetDone, components, volumeMap);

                    // key: volumeName, value: snapshotVolumePath 
                    IDictionary<string, string> snapshotVolumeMap = new Dictionary<string, string>();

                    foreach (var kv in snapshots)
                        snapshotVolumeMap.Add(kv.Key, vss.GetSnapshotProperties(kv.Value).SnapshotDeviceObject);

                    BackupFiles(components, volumeMap, snapshotVolumeMap, vmNamesMapSubset, options);

                    foreach (var component in components)
                        vss.SetBackupSucceeded(wm.InstanceId, wm.WriterId, component.Type, component.LogicalPath, component.ComponentName, true);

                    vss.BackupComplete();

                    RaiseEvent(EventAction.DeletingSnapshotSet, components, volumeMap);
                    vss.DeleteSnapshotSet(vssSet, true);
                }
            }
        }

        private void RaiseEvent(EventAction action, IList<IVssWMComponent> components, IDictionary<string, string> volumeMap)
        {
            if (BackupProgress != null)
            {
                var ebp = new BackupProgressEventArgs()
                {
                    Action = action
                };

                if (components != null)
                {
                    ebp.Components = new Dictionary<string, string>();
                    foreach (var component in components)
                        ebp.Components.Add(component.ComponentName, component.Caption);
                }

                if (volumeMap != null)
                {
                    ebp.VolumeMap = new Dictionary<string, string>();
                    foreach (var volume in volumeMap)
                        ebp.VolumeMap.Add(volume);
                }

                BackupProgress(this, ebp);
                if (ebp.Cancel)
                    throw new BackupCancelledException();
            }
        }


        private static string _sevenZipCurrentFile = string.Empty;

        private void BackupFiles(IList<IVssWMComponent> components, IDictionary<string, string> volumeMap,
                                   IDictionary<string, string> snapshotVolumeMap, IDictionary<string, string> vmNamesMap,
                                   Options options)
        {
            IList<System.IO.Stream> streams = new List<System.IO.Stream>();
            try
            {
                foreach (var component in components)
                {
                    string vmBackupPath;

                    if (options.DirectCopy)
                    {
                        vmBackupPath = Path.Combine(options.Output,
                            string.Format(options.OutputFormat, vmNamesMap[component.ComponentName],
                                component.ComponentName,
                                DateTime.Now, ""));
                    }
                    else
                    {
                        vmBackupPath = Path.Combine(options.Output,
                            string.Format(options.OutputFormat, vmNamesMap[component.ComponentName],
                                component.ComponentName,
                                DateTime.Now,
                                options.ZipFormat ? ".zip" : ".7z"));
                        File.Delete(vmBackupPath);
                    }

                    var files = new Dictionary<string, System.IO.Stream>();

                    foreach (var file in component.Files)
                    {
                        string path;
                        if (file.IsRecursive)
                            path = file.Path;
                        else
                            path = Path.Combine(file.Path, file.FileSpecification);

                        // Get the longest matching path
                        var volumePath = volumeMap.Keys.OrderBy(o => o.Length).Reverse().First(o => path.StartsWith(o, StringComparison.OrdinalIgnoreCase));
                        var volumeName = volumeMap[volumePath];


                        // Exclude snapshots
                        var fileName = Path.GetFileName(path.Substring(volumePath.Length)).ToUpperInvariant();
                        var include = !path.EndsWith("\\*");

                        var pathItems = path.Split(Path.DirectorySeparatorChar);
                        if (pathItems.Length >= 2)
                        {
                            if (pathItems[pathItems.Length - 2].ToLowerInvariant() == "snapshots")
                                include = false;
                        }

                        if (include && options.VhdInclude != null)
                        {
                            if (options.VhdInclude.Count(x => string.CompareOrdinal(x.ToUpperInvariant(), fileName) == 0) == 0)
                                include = false;
                        }

                        if (include && options.VhdIgnore != null)
                        {
                            if (options.VhdIgnore.Count(x => string.CompareOrdinal(x.ToUpperInvariant(), fileName) == 0) != 0)
                                include = false;
                        }

                        if (include)
                        {
                            if (options.DirectCopy)
                            {
                                DoDirectCopy(vmBackupPath, snapshotVolumeMap[volumeName], volumePath.Length, path);
                            }
                            else
                            {
                                AddPathToSevenZip(files, streams, snapshotVolumeMap[volumeName], volumePath.Length, path);
                            }
                        }
                        else
                            Console.WriteLine("Ignoring file {0}", path);
                    }

                    if (!options.DirectCopy)
                    {
                        SevenZipBase.SetLibraryPath(Path.Combine(AppDomain.CurrentDomain.SetupInformation.ApplicationBase, "7z.dll"));

                        var sevenZip = new SevenZipCompressor
                        {
                            ArchiveFormat = options.ZipFormat ? OutArchiveFormat.Zip : OutArchiveFormat.SevenZip,
                            CompressionMode = CompressionMode.Create,
                            DirectoryStructure = true,
                            PreserveDirectoryRoot = false
                        };
                        sevenZip.CustomParameters.Add("mt", "on");


                        switch (options.CompressionLevel)
                        {
                            case 0:
                                sevenZip.CompressionLevel = CompressionLevel.None;
                                break;
                            case 1:
                                sevenZip.CompressionLevel = CompressionLevel.Fast;
                                break;
                            case 2:
                                sevenZip.CompressionLevel = CompressionLevel.Fast;
                                break;
                            case 3:
                                sevenZip.CompressionLevel = CompressionLevel.Low;
                                break;
                            case 4:
                                sevenZip.CompressionLevel = CompressionLevel.Low;
                                break;
                            case 5:
                                sevenZip.CompressionLevel = CompressionLevel.Low;
                                break;
                            case 6:
                                sevenZip.CompressionLevel = CompressionLevel.Normal;
                                break;
                            case 7:
                                sevenZip.CompressionLevel = CompressionLevel.High;
                                break;
                            case 8:
                                sevenZip.CompressionLevel = CompressionLevel.High;
                                break;
                            case 9:
                                sevenZip.CompressionLevel = CompressionLevel.Ultra;
                                break;
                        }

                        if (BackupProgress != null)
                        {
                            sevenZip.FileCompressionStarted += (sender, e) =>
                            {
                                var ebp = new BackupProgressEventArgs()
                                {
                                    AcrhiveFileName = e.FileName,
                                    Action = EventAction.StartingArchive
                                };

                                _sevenZipCurrentFile = e.FileName;

                                Report7ZipProgress(component, volumeMap, ebp);

                                if (_cancel)
                                {
                                    e.Cancel = true;
                                }
                            };

                            sevenZip.FileCompressionFinished += (sender, e) =>
                            {
                                var ebp = new BackupProgressEventArgs
                                {
                                    AcrhiveFileName = _sevenZipCurrentFile,
                                    Action = EventAction.ArchiveDone
                                };

                                _sevenZipCurrentFile = string.Empty;

                                Report7ZipProgress(component, volumeMap, ebp);
                            };

                            sevenZip.Compressing += (sender, e) =>
                            {
                                var ebp = new BackupProgressEventArgs
                                {
                                    AcrhiveFileName = _sevenZipCurrentFile,
                                    Action = EventAction.PercentProgress,
                                    CurrentEntry = _sevenZipCurrentFile,
                                    PercentDone = e.PercentDone
                                };

                                Report7ZipProgress(component, volumeMap, ebp);

                                if (_cancel)
                                {
                                    e.Cancel = true;
                                }
                            };
                        }

                        if (string.IsNullOrEmpty(options.Password))
                            sevenZip.CompressStreamDictionary(files, vmBackupPath);
                        else
                            sevenZip.CompressStreamDictionary(files, vmBackupPath, options.Password);

                        if (_cancel)
                        {
                            if (File.Exists(vmBackupPath))
                            {
                                File.Delete(vmBackupPath);
                            }
                            throw new BackupCancelledException();
                        }
                    }
                }
            }
            finally
            {
                // Make sure that all streams are closed
                foreach (var s in streams)
                    s.Close();
            }
        }

        private static void AddPathToSevenZip(Dictionary<string, System.IO.Stream> files, IList<System.IO.Stream> streams, string snapshotPath, int volumePathLength, string vmPath)
        {
            var srcPath = Path.Combine(snapshotPath, vmPath.Substring(volumePathLength));

            if (Directory.Exists(srcPath))
            {
                foreach (var srcChildPath in Directory.GetFileSystemEntries(srcPath))
                {
                    var srcChildPathRel = srcChildPath.Substring(snapshotPath.EndsWith(Path.PathSeparator.ToString(), StringComparison.CurrentCultureIgnoreCase) ? snapshotPath.Length : snapshotPath.Length + 1);
                    var childPath = Path.Combine(vmPath.Substring(0, volumePathLength), srcChildPathRel);
                    AddPathToSevenZip(files, streams, snapshotPath, volumePathLength, childPath);
                }
            }
            else if (File.Exists(srcPath))
            {
                var s = File.OpenRead(srcPath);
                files.Add(vmPath.Substring(volumePathLength), s);
                streams.Add(s);
            }
            else
            {
                var lowerPath = srcPath.ToLowerInvariant();
                var isIgnorable = lowerPath.EndsWith(".avhdx") || lowerPath.EndsWith(".vmrs") ||
                                lowerPath.EndsWith(".bin") || lowerPath.EndsWith(".vsv");

                if (!isIgnorable)
                    throw new Exception($"Entry \"{srcPath}\" not found in snapshot");
            }
        }


        private static void DoDirectCopy(string vmBackupPath, string snapshotPath, int volumePathLength, string vmPath)
        {
            var srcPath = Path.Combine(snapshotPath, vmPath.Substring(volumePathLength));

            if (Directory.Exists(srcPath))
            {
                foreach (var srcChildPath in Directory.GetFileSystemEntries(srcPath))
                {
                    var srcChildPathRel = srcChildPath.Substring(snapshotPath.EndsWith(Path.PathSeparator.ToString(), StringComparison.CurrentCultureIgnoreCase) ? snapshotPath.Length : snapshotPath.Length + 1);
                    var childPath = Path.Combine(vmPath.Substring(0, volumePathLength), srcChildPathRel);
                    DoDirectCopy(vmBackupPath, snapshotPath, volumePathLength, childPath);
                }
            }
            else if (File.Exists(srcPath))
            {
                var outputName = Path.Combine(vmBackupPath, vmPath.Substring(volumePathLength));
                var s = File.OpenRead(srcPath);

                var folder = Path.GetDirectoryName(outputName);
                if (!Directory.Exists(folder))
                    Directory.CreateDirectory(folder);

                var ns = File.Create(outputName);
                s.CopyTo(ns);
            }
            else
            {
                var lowerPath = srcPath.ToLowerInvariant();
                var isIgnorable = lowerPath.EndsWith(".avhdx") || lowerPath.EndsWith(".vmrs") ||
                                lowerPath.EndsWith(".bin") || lowerPath.EndsWith(".vsv");

                if (!isIgnorable)
                    throw new Exception($"Entry \"{srcPath}\" not found in snapshot");
            }
        }


        protected bool UseWMIV2NameSpace
        {
            get
            {
                var version = Environment.OSVersion.Version;
                return version.Major >= 6 && version.Minor >= 2;
            }
        }

        protected string GetWMIScope(string host = "localhost")
        {
            string scopeFormatStr;
            if (UseWMIV2NameSpace)
                scopeFormatStr = "\\\\{0}\\root\\virtualization\\v2";
            else
                scopeFormatStr = "\\\\{0}\\root\\virtualization";

            return (string.Format(scopeFormatStr, host));
        }

        IDictionary<string, string> GetVMNames(IEnumerable<string> vmNames, VmNameType nameType)
        {
            IDictionary<string, string> d = new Dictionary<string, string>();

            string query;
            string vmIdField;

            if (UseWMIV2NameSpace)
            {
                query = "SELECT VirtualSystemIdentifier, ElementName FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
                vmIdField = "VirtualSystemIdentifier";
            }
            else
            {
                query = "SELECT SystemName, ElementName FROM Msvm_VirtualSystemSettingData WHERE SettingType = 3";
                vmIdField = "SystemName";
            }

            var inField = nameType == VmNameType.ElementName ? "ElementName" : vmIdField;

            var scope = new ManagementScope(GetWMIScope());

            if (vmNames != null && vmNames.Count() > 0)
                query += string.Format(" AND ({0})", GetORStr(inField, vmNames));

            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query)))
            {
                using (var moc = searcher.Get())
                    foreach (var mo in moc)
                        using (mo)
                            d.Add((string)mo[vmIdField], (string)mo["ElementName"]);
            }

            return d;
        }

        private static string GetORStr(string fieldName, IEnumerable<string> vmNames)
        {
            var sb = new StringBuilder();
            foreach (var vmName in vmNames)
            {
                if (sb.Length > 0)
                    sb.Append(" OR ");
                sb.Append(string.Format("{0} = '{1}'", fieldName, EscapeWMIStr(vmName)));
            }
            return sb.ToString();
        }

        private static string EscapeWMIStr(string str)
        {
            return str != null ? str.Replace("'", "''") : null;
        }

        private void Report7ZipProgress(IVssWMComponent component, IDictionary<string, string> volumeMap, BackupProgressEventArgs ebp)
        {
            if (ebp == null)
                return;

            ebp.Components = new Dictionary<string, string>();
            ebp.Components.Add(component.ComponentName, component.Caption);

            ebp.VolumeMap = new Dictionary<string, string>();
            foreach (var volume in volumeMap)
                ebp.VolumeMap.Add(volume.Key, volume.Value);

            BackupProgress(this, ebp);

            if (ebp.Cancel)
            {
                _cancel = true;
            }
        }
    }
}
