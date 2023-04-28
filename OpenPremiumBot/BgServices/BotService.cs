using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenPremiumBot.BgServices.Base;
using OpenPremiumBot.BgServices.BotHandler;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;

namespace OpenPremiumBot.BgServices
{
    public class BotService : MyBackgroundService
    {
        private readonly ITelegramBotClient _client;
        private readonly IFreeSql _freeSql;
        private readonly IConfiguration _configuration;
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public BotService(ITelegramBotClient client,
            IFreeSql freeSql,
            IConfiguration configuration,
            IServiceScopeFactory serviceScopeFactory)
        {
            _client = client;
            _freeSql = freeSql;
            _configuration = configuration;
            _serviceScopeFactory = serviceScopeFactory;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var receiverOptions = new ReceiverOptions()
            {
                AllowedUpdates = Array.Empty<UpdateType>(),
                ThrowPendingUpdates = true,
            };
            UpdateHandlers.freeSql = _freeSql;
            UpdateHandlers.configuration = _configuration;
            UpdateHandlers.ServiceProvider = _serviceScopeFactory.CreateScope().ServiceProvider;
            _client.StartReceiving(updateHandler: UpdateHandlers.HandleUpdateAsync,
                   pollingErrorHandler: UpdateHandlers.PollingErrorHandler,
                   receiverOptions: receiverOptions,
                   cancellationToken: stoppingToken);
            return Task.CompletedTask;
        }
    }
}
