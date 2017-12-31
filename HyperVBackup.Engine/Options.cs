using System.Collections.Generic;

namespace HyperVBackUp.Engine
{
    public class Options
    {
        public string Password { get; set; }
        public bool SingleSnapshot { get; set; }
        public string Output { get; set; }
        public string OutputFormat { get; set; }
        public IList<string> VhdInclude { get; set; }
        public IList<string> VhdIgnore { get; set; }
        public IList<string> Exclude { get; set; }
        public int CompressionLevel { get; set; }
        public bool ZipFormat { get; set; }
        public bool DirectCopy { get; set; }
        public bool MultiThreaded { get; set; }
    }
}
