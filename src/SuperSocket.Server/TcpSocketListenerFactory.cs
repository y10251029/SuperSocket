using Microsoft.Extensions.Logging;
using SuperSocket.Channel;
using SuperSocket.ProtoBase;

namespace SuperSocket.Server
{
    public class TcpSocketListenerFactory : IListenerFactory
    {
        public IListener CreateListener<TPackageInfo>(ListenOptions options, ILoggerFactory loggerFactory, object pipelineFilterFactory)
            where TPackageInfo : class
        {
            var filterFactory = pipelineFilterFactory as IPipelineFilterFactory<TPackageInfo>;
            return new TcpSocketListener(options, (s) => new TcpPipeChannel<TPackageInfo>(s, filterFactory.Create(s), loggerFactory.CreateLogger(nameof(IChannel))), loggerFactory.CreateLogger(nameof(TcpSocketListener)));
        }
    }
}