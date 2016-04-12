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
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace HyperVBackUp.Engine
{
    public class CSV
    {
        [DllImport("ResUtils.dll", CharSet = CharSet.Auto)]
        private static extern bool ClusterIsPathOnSharedVolume(string lpszPathName);

        [DllImport("ResUtils.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool ClusterGetVolumePathName(string lpszFileName, StringBuilder lpszVolumePathName, int cchBufferLength);

        [DllImport("ResUtils.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool ClusterGetVolumeNameForVolumeMountPoint(string lpszVolumeMountPoint, StringBuilder lpszVolumeName, int cchBufferLength);

        [DllImport("ResUtils.dll", CharSet = CharSet.Auto)]
        private static extern int ClusterPrepareSharedVolumeForBackup(string lpszFileName, StringBuilder lpszVolumePathName, ref int lpcchVolumePathName, StringBuilder lpszVolumeName, ref int lpcchVolumeName);

        [DllImport("ResUtils.dll", CharSet = CharSet.Auto)]
        private static extern int ClusterClearBackupStateForSharedVolume(string lpszVolumePathName);

        public static bool IsSupported()
        {
            // Note: should also check for Server edition
            return (Environment.OSVersion.Platform == PlatformID.Win32NT && Environment.OSVersion.Version.Major >= 6 && Environment.OSVersion.Version.Minor >= 1);
        }

        public static bool IsPathOnSharedVolume(string path)
        {
            return ClusterIsPathOnSharedVolume(path);
        }

        public static string GetVolumeNameForMountPoint(string volumeMountPoint)
        {
            StringBuilder sb = new StringBuilder(2048);
            if (!ClusterGetVolumeNameForVolumeMountPoint(volumeMountPoint, sb, sb.Capacity))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return sb.ToString();
        }

        public static string GetVolumePath(string path)
        {
            StringBuilder sb = new StringBuilder(2048);
            if (!ClusterGetVolumePathName(path, sb, sb.Capacity))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return sb.ToString();
        }

        public static void ClusterPrepareSharedVolumeForBackup(string path, out string volumePath, out string volumeName)
        {
            StringBuilder sbVolumePath = new StringBuilder(2048);
            StringBuilder sbVolumeName = new StringBuilder(2048);

            int volumePathCapacity = sbVolumePath.Capacity;
            int volumeNameCapacity = sbVolumeName.Capacity;

            int err = ClusterPrepareSharedVolumeForBackup(path, sbVolumePath, ref volumePathCapacity, sbVolumeName, ref volumeNameCapacity);
            if (err != 0)
                throw new Win32Exception(err);

            volumePath = sbVolumePath.ToString();
            volumeName = sbVolumeName.ToString();
        }
    }
}
