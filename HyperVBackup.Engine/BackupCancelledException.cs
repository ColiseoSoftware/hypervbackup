using System;

namespace HyperVBackUp.Engine
{
    public class BackupCancelledException : Exception
    {
        public BackupCancelledException() : base("Backup cancelled")
        {
        }

        public BackupCancelledException(string message) : base(message)
        {
        }

        public BackupCancelledException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected BackupCancelledException(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) : base(info, context)
        {
        }
    }
}
