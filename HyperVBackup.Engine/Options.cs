﻿using System.Collections.Generic;

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
        public int CompressionLevel { get; set; }
    }
}
