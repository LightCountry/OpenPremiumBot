using Flurl;
using Flurl.Http;
using FreeSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenPremiumBot.BgServices.Base;
using OpenPremiumBot.BgServices.BotHandler;
using OpenPremiumBot.Domains;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using static FreeSql.Internal.GlobalFilter;

namespace OpenPremiumBot.BgServices
{
    public class AutoCancelOrderService : BaseScheduledService
    {
        private readonly IConfiguration _configuration;
        private readonly IServiceProvider _serviceProvider;
        private readonly ITelegramBotClient _botClient;
        private readonly IFreeSql _freeSql;
        private readonly ILogger<AutoCancelOrderService> _logger;
        private readonly FlurlClient client;
        public AutoCancelOrderService(
            IConfiguration configuration,
            IServiceProvider serviceProvider,
            ITelegramBotClient telegramBot,
            IFreeSql freeSql,
            ILogger<AutoCancelOrderService> logger) : base("定时取消订单", TimeSpan.FromMinutes(10), logger)
        {
            _configuration = configuration;
            _serviceProvider = serviceProvider;
            _botClient = telegramBot;
            _freeSql = freeSql;
            _logger = logger;

        }

        protected override async Task ExecuteAsync()
        {
            var curd = _freeSql.GetRepository<Orders>();
            var time = DateTime.Now.AddMinutes(-30);
            var orders = await curd.Where(x => x.CreateTime < time && x.OrderStatus == OrderStatus.待付款).ToListAsync();
            if (orders.Count > 0)
                _logger.LogInformation("共有{a}个待取消订单", orders.Count);
            foreach (var order in orders)
            {
                _logger.LogInformation("系统自动取消订单:{id}", order.Id);
                try
                {
                    order.FailMemo = "超时未支付，系统自动取消！";
                    order.IsDeleted = true;
                    order.DeletedTime = DateTime.Now;
                    await curd.UpdateAsync(order);
                    var senText = $@"<b>您的订单已被系统取消！</b>

订单号：<code>{order.Id}</code>
下单用户：<code>{order.UserId}</code>
下单时间：<code>{order.CreateTime:yyyy-MM-dd HH:mm}</code>
TG用户名：<b>{order.AccountInfo}</b>
开通时长：<b>{order.Months}</b>个月Telegram Premium会员

取消原因：<b>{(string.IsNullOrEmpty(order.FailMemo) ? "无" : order.FailMemo)}</b>

<b>如有疑问，请联系客服！</b>
";
                    InlineKeyboardMarkup inlineKeyboard = new(
                        new[]
                        {
                        new[]
                        {
                            InlineKeyboardButton.WithUrl("联系客服", UpdateHandlers.AdminUserUrl)
                        }
                        });
                    await _botClient.SendTextMessageAsync(order.UserId, senText, ParseMode.Html, replyMarkup: inlineKeyboard);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "订单超时处理失败！");
                }
                finally
                {
                    await Task.Delay(1000);//等待一秒
                }
            }
        }
    }
}
