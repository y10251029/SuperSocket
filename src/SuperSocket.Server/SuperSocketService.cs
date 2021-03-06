using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperSocket;
using SuperSocket.Channel;
using SuperSocket.ProtoBase;

namespace SuperSocket.Server
{
    public class SuperSocketService<TReceivePackageInfo, TPipelineFilterFactory> : SuperSocketService<TReceivePackageInfo>
        where TReceivePackageInfo : class
        where TPipelineFilterFactory : IPipelineFilterFactory<TReceivePackageInfo>, new()
    {
        public SuperSocketService(IServiceProvider serviceProvider, IOptions<ServerOptions> serverOptions, ILoggerFactory loggerFactory, IListenerFactory listenerFactory, Func<IAppSession, TReceivePackageInfo, Task> packageHandler)
            : base(serviceProvider, serverOptions, loggerFactory, listenerFactory, packageHandler)
        {

        }

        protected override IPipelineFilterFactory<TReceivePackageInfo> GetPipelineFilterFactory()
        {
            return new TPipelineFilterFactory();
        }
    }

    public class SuperSocketService<TReceivePackageInfo> : IHostedService, IServer
        where TReceivePackageInfo : class
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IOptions<ServerOptions> _serverOptions;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;
        private IPipelineFilterFactory<TReceivePackageInfo> _pipelineFilterFactory;
        private IListenerFactory _listenerFactory;
        private List<IListener> _listeners;
        private Func<IAppSession, TReceivePackageInfo, Task> _packageHandler;
        private int _sessionCount;
        public string Name { get; }
        public int SessionCount => _sessionCount;

        private IMiddleware _rootMiddleware;

        public SuperSocketService(IServiceProvider serviceProvider, IOptions<ServerOptions> serverOptions, ILoggerFactory loggerFactory, IListenerFactory listenerFactory, Func<IAppSession, TReceivePackageInfo, Task> packageHandler)
        {
            _serverOptions = serverOptions;
            Name = serverOptions.Value.Name;
            _serviceProvider = serviceProvider;
            _pipelineFilterFactory = GetPipelineFilterFactory();
            _serverOptions = serverOptions;
            _loggerFactory = loggerFactory;
            _logger = _loggerFactory.CreateLogger("SuperSocketService");
            _listenerFactory = listenerFactory;
            _packageHandler = packageHandler;
        }

        protected virtual IPipelineFilterFactory<TReceivePackageInfo> GetPipelineFilterFactory()
        {
            return _serviceProvider.GetRequiredService<IPipelineFilterFactory<TReceivePackageInfo>>();
        }

        private Task<bool> StartListenAsync()
        {
            _listeners = new List<IListener>();

            foreach (var l in _serverOptions.Value.Listeners)
            {
                var listener = _listenerFactory.CreateListener<TReceivePackageInfo>(l, _loggerFactory, _pipelineFilterFactory);
                listener.NewClientAccepted += OnNewClientAccept;
                
                if (!listener.Start())
                {
                    _logger.LogError($"Failed to listen {listener}.");
                    continue;
                }

                _listeners.Add(listener);
            }

            return Task.FromResult(true);
        }

        protected virtual void OnNewClientAccept(IListener listener, IChannel channel)
        {
            var session = new AppSession(this, channel);
            InitializeSession(session);            
            HandleSession(session).DoNotAwait();
        }

        private void InitializeSession(AppSession session)
        {
            var packageHandler = _packageHandler;

            if (packageHandler != null)
            {
                if (session.Channel is IChannel<TReceivePackageInfo> channel)
                {
                    channel.PackageReceived += async (ch, p) =>
                    {
                        try
                        {
                            await packageHandler(session, p);
                        }
                        catch (Exception e)
                        {
                            OnSessionError(session, e);
                        }
                    };
                }
            }
        }

        private async Task HandleSession(AppSession session)
        {
            Interlocked.Increment(ref _sessionCount);

            try
            {
                _logger.LogInformation($"A new session connected: {session.SessionID}");
                await session.Channel.StartAsync();
                _logger.LogInformation($"The session disconnected: {session.SessionID}");
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to handle the session {session.SessionID}.", e);
            }
            
            Interlocked.Decrement(ref _sessionCount);
        }

        protected virtual void OnSessionError(IAppSession session, Exception exception)
        {
            _logger.LogError($"Session[{session.SessionID}]: session exception.", exception);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await StartListenAsync();
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            var tasks = _listeners.Where(l => l.IsRunning).Select(l => l.StopAsync()).ToArray();
            await Task.WhenAll(tasks);
        }

        async Task<bool> IServer.StartAsync()
        {
            await StartAsync(CancellationToken.None);
            return true;
        }

        async Task IServer.StopAsync()
        {
            await StopAsync(CancellationToken.None);
        }

        public void UseMiddleware<TMiddleware>()
            where TMiddleware : IMiddleware
        {
            var middleware = _serviceProvider.GetService<TMiddleware>();

            IMiddleware lastMiddleware = _rootMiddleware;

            while (lastMiddleware != null)
            {
                if (lastMiddleware.Next == null)
                    break;

                lastMiddleware = lastMiddleware.Next;
            }

            if (lastMiddleware == null)
                _rootMiddleware = middleware;
            else
                lastMiddleware.Next = middleware;
        }
    }
}
