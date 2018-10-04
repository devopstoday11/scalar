﻿using GVFS.Common.FileSystem;
using GVFS.Common.Tracing;
using System.IO;

namespace GVFS.Common
{
    public class LocalGVFSConfig
    {
        private const string FileName = "gvfs.config";
        private readonly string configFile;
        private readonly PhysicalFileSystem fileSystem;
        private FileBasedDictionary<string, string> allSettings;

        public LocalGVFSConfig()
        {
            string servicePath = Paths.GetServiceDataRoot(GVFSConstants.Service.ServiceName);
            string gvfsDirectory = Path.GetDirectoryName(servicePath);

            this.configFile = Path.Combine(gvfsDirectory, FileName);
            this.fileSystem = new PhysicalFileSystem();
        }

        public bool TryGetConfig(
            string key, 
            out string value, 
            out string error,
            ITracer tracer)
        {
            if (!this.TryLoadSettings(tracer, out error))
            {
                error = $"Error getting config value {key}. {error}";
                value = null;
                return false;
            }
                        
            try
            {
                this.allSettings.TryGetValue(key.ToUpper(), out value);
                error = null;
                return true;
            }
            catch (FileBasedCollectionException exception)
            {
                const string ErrorFormat = "Error getting config value for {0}. Config file {1}. {2}";
                if (tracer != null)
                {
                    tracer.RelatedError(ErrorFormat, key, this.configFile, exception.ToString());
                }

                error = string.Format(ErrorFormat, key, this.configFile, exception.Message);
                value = null;
                return false;
            }
        }

        public bool TrySetConfig(
            string key, 
            string value, 
            out string error,
            ITracer tracer)
        {
            if (!this.TryLoadSettings(tracer, out error))
            {
                error = $"Error setting config value {key}: {value}. {error}";
                return false;
            }

            try
            {
                this.allSettings.SetValueAndFlush(key.ToUpper(), value);
                error = null;
                return true;
            }
            catch (FileBasedCollectionException exception)
            {
                const string ErrorFormat = "Error setting config value {0}: {1}. Config file {2}. {3}";
                if (tracer != null)
                {
                    tracer.RelatedError(ErrorFormat, key, value, this.configFile, exception.ToString());
                }

                error = string.Format(ErrorFormat, key, value, this.configFile, exception.Message);
                value = null;
                return false;
            }
        }

        private bool TryLoadSettings(ITracer tracer, out string error)
        {
            if (this.allSettings == null)
            {
                FileBasedDictionary<string, string> config = null;
                if (FileBasedDictionary<string, string>.TryCreate(
                    tracer: tracer,
                    dictionaryPath: this.configFile,
                    fileSystem: this.fileSystem,
                    output: out config,
                    error: out error))
                {
                    this.allSettings = config;
                    return true;
                }

                return false;
            }

            error = null;
            return true;
        }
    }
}
