using System;

namespace ModManager.Core.Contracts.Services
{
    public interface IAppLogger
    {
        void Error(string message, Exception? ex = null, bool notify = true);
        void Warn(string message, bool notify = false);
        void Info(string message);
    }
}
