using System;
using System.Collections.Generic;

namespace HyperVBackUp.Engine
{
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
}
