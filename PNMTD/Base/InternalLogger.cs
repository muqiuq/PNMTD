using PNMTD.Notifications;

namespace PNMTD.Base
{
    public class InternalLogger<T>
    {

        private static ILogger _logger;
        public static ILogger Logger
        {
            get
            {
                if (_logger == null)
                {
                    _logger = Global.CreateLogger<T>();
                }
                return _logger;
            }
        }

    }
}
