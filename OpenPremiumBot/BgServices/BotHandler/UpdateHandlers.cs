using FreeSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SkiaSharp.QrCode.Image;
using SkiaSharp;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.InputFiles;
using Telegram.Bot.Types.ReplyMarkups;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Xml;
using System;
using System.Net;
using SkiaSharp.QrCode.Models;
using SkiaSharp.QrCode;
using System.Reflection.Metadata;
using System.Linq;
using OpenPremiumBot.Domains;

namespace OpenPremiumBot.BgServices.BotHandler;

public static class UpdateHandlers
{
    /// <summary>
    /// 记录当前订单账号信息
    /// </summary>
    public static ConcurrentDictionary<long, string> dic = new ConcurrentDictionary<long, string>();
    /// <summary>
    /// 记录当前开通时长
    /// </summary>
    public static ConcurrentDictionary<long, int> dicMonths = new ConcurrentDictionary<long, int>();
    /// <summary>
    /// 记录当前支付订单号
    /// </summary>
    public static ConcurrentDictionary<long, long> dicTradeNo = new ConcurrentDictionary<long, long>();
    public static IConfiguration configuration = null!;
    public static IFreeSql freeSql = null!;
    public static IServiceProvider ServiceProvider = null!;
    public static long AdminUserId => configuration.GetValue<long>("BotConfig:AdminUserId");
    public static string AdminUserUrl => configuration.GetValue<string>("BotConfig:AdminUserUrl") ?? "";

    public static Func<int, decimal> GetCNYPrice = months => configuration.GetValue<decimal>($"PriceCNY:{months}", months * 25);
    public static Func<int, decimal> GetUSDTPrice = months => configuration.GetValue<decimal>($"PriceUSDT:{months}", months * 3.99m);
    private static ReplyKeyboardMarkup menuReplyKeyboardMarkup = new(
            new[]
            {
                        new KeyboardButton[] { "开始下单", "个人信息", "最近订单"},
            })
    {
        ResizeKeyboard = true
    };
    private static ReplyKeyboardMarkup ConfirmMenuReplyKeyboardMarkup1 = new(
            new[]
            {
                        new KeyboardButton[] { "确认用户名"},
                        new KeyboardButton[] { "取消下单"},
            })
    {
        ResizeKeyboard = true
    };
    private static ReplyKeyboardMarkup ConfirmMenuReplyKeyboardMarkup2 = new(
            new[]
            {
                        new KeyboardButton[] { "三个月"},
                        new KeyboardButton[] { "六个月"},
                        new KeyboardButton[] { "十二个月"},
                        new KeyboardButton[] { "取消下单"},
            })
    {
        ResizeKeyboard = true
    };
    private static ReplyKeyboardMarkup ConfirmMenuReplyKeyboardMarkup3 = new(
            new[]
            {
                        new KeyboardButton[] { "确认订单"},
                        new KeyboardButton[] { "修改开通时长"},
                        new KeyboardButton[] { "取消下单"},
            })
    {
        ResizeKeyboard = true
    };
    /// <summary>
    /// 错误处理
    /// </summary>
    /// <param name="botClient"></param>
    /// <param name="exception"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task PollingErrorHandler(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var ErrorMessage = exception switch
        {
            ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

        Log.Error(exception, ErrorMessage);
        return Task.CompletedTask;
    }
    /// <summary>
    /// 处理更新
    /// </summary>
    /// <param name="botClient"></param>
    /// <param name="update"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        var handler = update.Type switch
        {
            UpdateType.Message => BotOnMessageReceived(botClient, update.Message!),
            UpdateType.CallbackQuery => BotOnCallbackQueryReceived(botClient, update.CallbackQuery!),
            _ => Task.CompletedTask
        };

        try
        {
            await handler;
        }
        catch (Exception exception)
        {
            await PollingErrorHandler(botClient, exception, cancellationToken);
        }
    }
    /// <summary>
    /// 消息接收
    /// </summary>
    /// <param name="botClient"></param>
    /// <param name="message"></param>
    /// <returns></returns>
    private static async Task BotOnMessageReceived(ITelegramBotClient botClient, Message message)
    {
        Log.Information($"Receive message type: {message.Type}");
        if (message.Text is not { } messageText)
            return;

        try
        {
            await InsertOrUpdateUserAsync(botClient, message);
        }
        catch (Exception e)
        {
            Log.Logger.Error(e, "更新Telegram用户信息失败！{@user}", message.From);
        }
        var action = messageText.Split(' ')[0] switch
        {
            "/start" => Start(botClient, message),
            "最近订单" => MyOrders(botClient, message),
            "开始下单" => CreateOrder(botClient, message),
            "个人信息" => MyInfo(botClient, message),
            "取消下单" => CancelOrder(botClient, message),
            "确认订单" => ConfirmOrder(botClient, message),
            "确认用户名" => ConfirmOrder1(botClient, message),
            "修改开通时长" => ConfirmOrder1(botClient, message),
            "三个月" => ConfirmOrder2(botClient, message, 3),
            "六个月" => ConfirmOrder2(botClient, message, 6),
            "十二个月" => ConfirmOrder2(botClient, message, 12),
            _ => Usage(botClient, message)
        };
        Message sentMessage = await action;
        Log.Information($"The message was sent with id: {sentMessage.MessageId}");

        //通用回复
        static async Task<Message> Usage(ITelegramBotClient botClient, Message message)
        {
            var UserId = message.ToUserId();
            var text = (message.Text ?? "").Trim();

            InlineKeyboardMarkup closeBtn = new(
                new[]
                {
                                new[]
                                {
                                    InlineKeyboardButton.WithCallbackData("关闭"),
                                }
                });
            if (dic.ContainsKey(UserId) && text.StartsWith("@"))
            {
                dic.AddOrUpdate(UserId, text, (key, oldValue) => text);
                return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                                text: @$"您输入的账号信息如下：
<b>{text}</b>

🔴请仔细核对上方Telagarm用户名
🔴如确认Telagarm用户名无误，可点击下方【确认用户名】
", parseMode: ParseMode.Html,
                                                                replyMarkup: ConfirmMenuReplyKeyboardMarkup1);
            }
            else if (dicTradeNo.ContainsKey(UserId))
            {
                var OrderId = dicTradeNo.GetValueOrDefault(UserId);
                var curd = freeSql.GetRepository<Orders>();
                var order = await curd.Where(x => x.Id == OrderId && x.UserId == UserId).Include(x => x.User).FirstAsync();
                if (order == null)
                {
                    dicTradeNo.TryRemove(UserId, out var _);
                    return message;
                }
                if (order.OrderStatus != OrderStatus.待付款)
                {
                    dicTradeNo.TryRemove(UserId, out var _);
                    return message;
                }
                var TradeNo = text.Trim();

                var ErrInputAction = async () =>
                {
                    var noText = "";
                    if (order.PayMethod == PayMethod.支付宝)
                    {
                        noText += @"支付【订单号】";
                    }
                    else if (order.PayMethod == PayMethod.微信)
                    {
                        noText += @"【转账单号】";
                    }
                    else if (order.PayMethod == PayMethod.USDT)
                    {
                        noText += @"【交易哈希】";
                    }
                    return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                                text: @$"<b>您的输入有误，请复制【{order.PayMethod}】支付记录中的{noText}！</b>

当前正在提交的订单金额为【<b>{order.CNY}CNY / {order.USDT}USDT</b>】，请在【{order.PayMethod}】中查找这笔付款记录。

<b>请重新输入：</b>", parseMode: ParseMode.Html,
                                                                replyMarkup: menuReplyKeyboardMarkup);
                };
                if (await curd.Where(x => x.TradeNo == TradeNo && x.OrderStatus == OrderStatus.完成).AnyAsync())
                {
                    return await ErrInputAction();
                }
                else if (order.PayMethod == PayMethod.支付宝 && text.StartsWith("20"))
                {
                    order.OrderStatus = OrderStatus.待处理;
                    order.TradeNo = TradeNo;
                    order.PayTime = DateTime.Now;
                    await curd.UpdateAsync(order);
                }
                else if (order.PayMethod == PayMethod.微信 && text.StartsWith("10"))
                {
                    order.OrderStatus = OrderStatus.待处理;
                    order.TradeNo = TradeNo;
                    order.PayTime = DateTime.Now;
                    await curd.UpdateAsync(order);
                }
                else if (order.PayMethod == PayMethod.USDT && text.Length == 64)
                {
                    order.OrderStatus = OrderStatus.待处理;
                    order.TradeNo = TradeNo;
                    order.PayTime = DateTime.Now;
                    await curd.UpdateAsync(order);
                }
                else
                {
                    return await ErrInputAction();
                }
                var senText = $@"订单号：<code>{order.Id}</code>
下单用户：<code>{order.UserId}</code>
下单时间：<code>{order.CreateTime:yyyy-MM-dd HH:mm}</code>
TG用户名：<b>{order.AccountInfo}</b>
开通时长：<b>{order.Months}</b>个月Telegram Premium会员

支付方式：<b>{order.PayMethod}</b>
支付时间：<b>{order.PayTime:yyyy-MM-dd HH:mm}</b>
支付单号：<code>{order.TradeNo}</code>

<b>感谢您选择本机器人为您开通Telegram Premium会员，您的订单我们会加急处理！</b>";
                InlineKeyboardMarkup inlineKeyboard = new(
                new[]
                {
                        new[]
                        {
                            InlineKeyboardButton.WithUrl("联系客服",AdminUserUrl)
                        }
                });
                dicTradeNo.TryRemove(UserId, out var _);
                await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                                text: senText, ParseMode.Html,
                                                                replyMarkup: inlineKeyboard);

                if (AdminUserId != 0)
                {
                    var count = await curd.Where(x => x.UserId == order.UserId && x.OrderStatus == OrderStatus.完成).CountAsync();
                    var sumCNY = await curd.Where(x => x.UserId == order.UserId && x.OrderStatus == OrderStatus.完成).SumAsync(x => x.CNY);
                    var adminText = $@"订单号：<code>{order.Id}</code>
下单用户：<code>{order.UserId}</code>
下单用户：<b>{(string.IsNullOrEmpty(order.User?.UserName) ? "" : "@")}{order.User?.UserName}</b>
下单用户：<code>{order.User?.FirstName} {order.User?.LastName}</code>
下单时间：<code>{order.CreateTime:yyyy-MM-dd HH:mm}</code>
<a href=""tg://user?id={order.UserId}"">查看此用户</a>

TG用户名：<b>{order.AccountInfo}</b>
开通时长：<b>{order.Months}</b>个月Telegram Premium会员
订单备注：<b>{(string.IsNullOrEmpty(order.Memo) ? "无" : order.Memo)}</b>

支付方式：<b>{order.PayMethod}</b>
支付金额：<b>{order.CNY}</b> CNY
支付时间：<b>{order.PayTime:yyyy-MM-dd HH:mm:ss}</b>
支付单号：<code>{order.TradeNo}</code>

下单次数：<b>{count}</b> 次
下单金额：<b>{sumCNY}</b> CNY
";
                    InlineKeyboardMarkup adminKeyboard = new(
                new[]
                {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("拒绝订单",$"AdminPayCancel|{order.Id}"),
                            InlineKeyboardButton.WithCallbackData("通知用户已完成",$"AdminPayDone|{order.Id}")
                        }
                });
                    await botClient.SendTextMessageAsync(chatId: AdminUserId,
                                                                text: adminText, ParseMode.Html,
                                                                replyMarkup: adminKeyboard);
                }
                return message;
            }
            else if (text.StartsWith("SetMemo ") || text.StartsWith("SetFailMemo "))
            {
                if (AdminUserId == UserId)
                {
                    var args = text.Split(" ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (args.Length == 3 && long.TryParse(args[1], out var OrderId))
                    {
                        var curd = freeSql.GetRepository<Orders>();
                        var order = await curd.Where(x => x.Id == OrderId).FirstAsync();
                        if (order != null)
                        {
                            if (text.StartsWith("SetFailMemo "))
                            {
                                order.FailMemo = args.Last().Trim();
                            }
                            else
                            {
                                order.Memo = args.Last().Trim();
                            }
                            await curd.UpdateAsync(order);
                            var item = await curd.Where(x => x.Id == OrderId).FirstAsync();
                            var orderText = @$"订单号: <code>{item.Id}</code>
下单时间：<b>{item.CreateTime:yyyy-MM-dd HH:mm}</b>
支付方式：<b>{item.PayMethod}</b>
订单状态：<b>{item.OrderStatus}</b>
TG用户名：<b>{order.AccountInfo}</b>
开通时长：<b>{order.Months}</b>个月Telegram Premium会员
订单备注：<b>{(string.IsNullOrEmpty(item.Memo) ? "无" : item.Memo)}</b>
拒绝原因：<b>{(string.IsNullOrEmpty(item.FailMemo) ? "无" : item.FailMemo)}</b>
";

                            InlineKeyboardMarkup viewOrder = new(
                                new[]
                                {
                                new[]
                                {
                                    InlineKeyboardButton.WithCallbackData("关闭"),
                                }
                                });
                            return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                                        text: orderText, parseMode: ParseMode.Html,
                                                                        replyMarkup: viewOrder);
                        }
                        else
                        {

                            return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                                        text: "订单不存在或已删除！",
                                                                        replyMarkup: menuReplyKeyboardMarkup);
                        }
                    }
                    else
                    {
                        return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                                    text: "参数有误！",
                                                                    replyMarkup: menuReplyKeyboardMarkup);
                    }
                }
            }
            else if (text.StartsWith("ClearMemo ") || text.StartsWith("ClearFailMemo "))
            {
                if (AdminUserId == UserId)
                {
                    var args = text.Split(" ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (args.Length == 2 && long.TryParse(args[1], out var OrderId))
                    {
                        var curd = freeSql.GetRepository<Orders>();
                        var order = await curd.Where(x => x.Id == OrderId).FirstAsync();
                        if (order != null)
                        {
                            if (text.StartsWith("ClearFailMemo "))
                            {
                                order.FailMemo = null;
                            }
                            else
                            {
                                order.Memo = null;
                            }
                            await curd.UpdateAsync(order);
                            var item = await curd.Where(x => x.Id == OrderId).FirstAsync();
                            var orderText = @$"订单号: <code>{item.Id}</code>
下单时间：<b>{item.CreateTime:yyyy-MM-dd HH:mm}</b>
支付方式：<b>{item.PayMethod}</b>
订单状态：<b>{item.OrderStatus}</b>
TG用户名：<b>{order.AccountInfo}</b>
开通时长：<b>{order.Months}</b>个月Telegram Premium会员

订单备注：<b>{(string.IsNullOrEmpty(item.Memo) ? "无" : item.Memo)}</b>
拒绝原因：<b>{(string.IsNullOrEmpty(item.FailMemo) ? "无" : item.FailMemo)}</b>
";

                            InlineKeyboardMarkup viewOrder = new(
                                new[]
                                {
                                new[]
                                {
                                    InlineKeyboardButton.WithCallbackData("关闭"),
                                }
                                });
                            return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                                        text: orderText, parseMode: ParseMode.Html,
                                                                        replyMarkup: viewOrder);
                        }
                        else
                        {

                            return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                                        text: "订单不存在或已删除！",
                                                                        replyMarkup: menuReplyKeyboardMarkup);
                        }
                    }
                    else
                    {
                        return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                                    text: "参数有误！",
                                                                    replyMarkup: menuReplyKeyboardMarkup);
                    }
                }
            }
            InlineKeyboardMarkup _inlineKeyboard = new(
            new[]
            {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("关闭"),
                            InlineKeyboardButton.WithUrl("联系客服",AdminUserUrl)
                        }
            });
            return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                        text: @"<b>无法识别的输入！</b>

🔴如您正在填写【用户名】，请按照要求来填写。
🔴如您正在下单，请尝试【取消订单】，然后重新点击【开始下单】。
🔴如您正在发送支付订单号，请尝试点击【我要重新支付】（无需您重新扫码付款），重新点击【我已支付】，然后再发送订单号。

🟢如需帮助，可点击下方按钮联系客服
", parseMode: ParseMode.Html,
                                                        replyMarkup: _inlineKeyboard);
        }
    }

    private static async Task<Message> ConfirmOrder2(ITelegramBotClient botClient, Message message, int months)
    {
        var UserId = message.ToUserId();
        dicMonths.AddOrUpdate(UserId, months, (key, oldValue) => months);
        dic.TryGetValue(UserId, out var UserName);
        return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                    text: @$"Telegram用户名：{UserName}
开通时长：{months}个月

请确认订单信息，如订单信息有误，请取消下单，然后重新下单：", parseMode: ParseMode.Html,
                                                    replyMarkup: ConfirmMenuReplyKeyboardMarkup3);
    }

    private static async Task<Message> ConfirmOrder1(ITelegramBotClient botClient, Message message)
    {
        return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                        text: "请选择开通时长：", parseMode: ParseMode.Html,
                                                        replyMarkup: ConfirmMenuReplyKeyboardMarkup2);
    }

    private static async Task<Message> MyInfo(ITelegramBotClient botClient, Message message)
    {
        var user = await message.ToUser();
        var curd = freeSql.GetRepository<Orders>();
        InlineKeyboardMarkup inlineKeyboard = new(
            new[]
            {
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("关闭"),
                        },
                        new[]
                        {
                            InlineKeyboardButton.WithUrl("联系客服",AdminUserUrl)
                        }
            });
        var OrderCount = await curd.Where(x => x.UserId == user.UserId && x.OrderStatus == OrderStatus.完成).CountAsync();
        var OrderPrice = await curd.Where(x => x.UserId == user.UserId && x.OrderStatus == OrderStatus.完成).SumAsync(x => x.CNY);
        return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                    text: @$"<b>个人信息</b>

TG昵称： <b>{user.FirstName} {user.LastName}</b>
TG ID： <code>{user.UserId}</code>

累计下单：<b>{OrderCount}</b> 单
累计支付：<b>{OrderPrice}</b> 元
", parseMode: ParseMode.Html,
                                                    replyMarkup: inlineKeyboard);
    }

    private static async Task<Message> Start(ITelegramBotClient botClient, Message message)
    {
        var user = await message.ToUser();
        return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                    text: @$"🔴本机器人向您提供Telegram Premium会员开通服务！

<b>价目表：</b>
三个月：<b>{GetCNYPrice(3)} CNY / {GetUSDTPrice(3)} USDT</b>
六个月：<b>{GetCNYPrice(6)} CNY / {GetUSDTPrice(6)} USDT</b>
十二个月：<b>{GetCNYPrice(12)} CNY / {GetUSDTPrice(12)} USDT</b>

请选择下方菜单：", parseMode: ParseMode.Html,
                                                    replyMarkup: menuReplyKeyboardMarkup);
    }
    private static async Task<Message> MyOrders(ITelegramBotClient botClient, Message message)
    {
        var UserId = message.ToUserId();
        var curd = freeSql.GetRepository<Orders>();
        var orders = await curd.Where(x => x.UserId == UserId && x.OrderStatus > OrderStatus.待付款).OrderByDescending(x => x.Id).Take(15).ToListAsync();
        InlineKeyboardMarkup inlineKeyboard = new(
            new[]
            {
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("关闭"),
                        },
                        new[]
                        {
                            InlineKeyboardButton.WithUrl("联系客服",AdminUserUrl)
                        }
            });
        var text = $@"<b>我的订单</b>

";
        if (orders.Count == 0)
        {
            text += "暂无订单！";
        }
        foreach (var item in orders)
        {
            if (item.OrderStatus == OrderStatus.完成)
            {
                text += @$"订单号: <code>{item.Id}</code>
下单时间：<b>{item.CreateTime:yyyy-MM-dd HH:mm}</b>
支付方式：<b>{item.PayMethod}</b>
订单状态：<b>{item.OrderStatus}</b>
订单金额：<b>{item.CNY}</b> 元 / <b>{item.USDT}</b> USDT
TG用户名：<b>{item.AccountInfo}</b>
开通时长：<b>{item.Months}</b>个月Telegram Premium会员
订单备注：<b>{(string.IsNullOrEmpty(item.Memo) ? "无" : item.Memo)}</b>
------------------------------
";
            }
            else if (item.OrderStatus == OrderStatus.拒绝)
            {
                text += @$"订单号: <code>{item.Id}</code>
下单时间：<b>{item.CreateTime:yyyy-MM-dd HH:mm}</b>
支付方式：<b>{item.PayMethod}</b>
订单状态：<b>{item.OrderStatus}</b>
订单金额：<b>{item.CNY}</b> 元 / <b>{item.USDT}</b> USDT
TG用户名：<b>{item.AccountInfo}</b>
开通时长：<b>{item.Months}</b>个月Telegram Premium会员
失败备注：<b>{(string.IsNullOrEmpty(item.Memo) ? "无" : item.Memo)}</b>
------------------------------
";
            }
            else
            {
                text += @$"订单号: <code>{item.Id}</code>
下单时间：<b>{item.CreateTime:yyyy-MM-dd HH:mm}</b>
支付方式：<b>{item.PayMethod}</b>
订单状态：<b>{item.OrderStatus}</b>
订单金额：<b>{item.CNY}</b> 元 / <b>{item.USDT}</b> USDT
TG用户名：<b>{item.AccountInfo}</b>
开通时长：<b>{item.Months}</b>个月Telegram Premium会员
------------------------------
";
            }
        }
        return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                    text: text, parseMode: ParseMode.Html,
                                                    replyMarkup: inlineKeyboard);
    }
    private static async Task<Message> CancelOrder(ITelegramBotClient botClient, Message message)
    {
        var UserId = message.ToUserId();
        dic.TryRemove(UserId, out var _);
        return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                    text: @"取消成功，欢迎下次光临！",
                                                    parseMode: ParseMode.Html,
                                                    replyMarkup: menuReplyKeyboardMarkup);
    }
    private static async Task<Message> ConfirmOrder(ITelegramBotClient botClient, Message message)
    {
        var UserId = message.ToUserId();
        if (!dic.ContainsKey(UserId))
        {
            return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                        text: @"请点击下方【开始下单】", parseMode: ParseMode.Html,
                                                        replyMarkup: menuReplyKeyboardMarkup);
        }
        var curd2 = freeSql.GetRepository<Orders>();

        var user = await message.ToUser();
        dic.TryRemove(UserId, out var AccountInfo);
        if (string.IsNullOrEmpty(AccountInfo) || !AccountInfo.StartsWith("@"))
        {
            return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                        text: @"您输入的账号信息有误，请重新下单！", parseMode: ParseMode.Html,
                                                        replyMarkup: menuReplyKeyboardMarkup);
        }
        dicMonths.TryRemove(UserId, out var months);
        var _order = new Orders
        {
            UserId = UserId,
            CNY = GetCNYPrice(months),
            USDT = GetUSDTPrice(months),
            OrderStatus = OrderStatus.待付款,
            CreateTime = DateTime.Now,
            AccountInfo = AccountInfo,
            Months = months
        };
        var order = await curd2.InsertAsync(_order);

        var m = await botClient.SendTextMessageAsync(chatId: message.Chat.Id, "订单正在创建中...", replyMarkup: menuReplyKeyboardMarkup);
        await Task.Delay(1000);
        await DeleteMessageAsync(botClient, m.Chat.Id, m.MessageId);
        await botClient.SendTextMessageAsync(message.Chat.Id, "订单创建完成！", replyMarkup: menuReplyKeyboardMarkup);
        InlineKeyboardMarkup inlineKeyboard = new(
            new[]
            {
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("立即支付",$"PayOrder|{order.Id}"),
                            InlineKeyboardButton.WithCallbackData("取消订单",$"CancelOrder|{order.Id}"),
                        },
                        new []
                        {
                            InlineKeyboardButton.WithUrl("联系客服",AdminUserUrl)
                        }
            });
        return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                    text: @$"<b>订单创建成功！</b>

订单号：<code>{order.Id}</code>
下单用户：<code>{order.UserId}</code>
下单时间：<b>{order.CreateTime:yyyy-MM-dd HH:mm}</b>
订单金额：<b>{order.CNY}</b> 元 / <b>{order.USDT}</b> USDT
TG用户名：<b>{order.AccountInfo}</b>
开通时长：<b>{order.Months}</b>个月Telegram Premium会员
订单备注：<b>{(string.IsNullOrEmpty(order.Memo) ? "无" : order.Memo)}</b>

", parseMode: ParseMode.Html,
                                                    replyMarkup: inlineKeyboard);
    }
    private static async Task<Message> CreateOrder(ITelegramBotClient botClient, Message message)
    {
        var UserId = message.ToUserId();
        var curdOrder = freeSql.GetRepository<Orders>();
        var order = curdOrder.Where(x => x.OrderStatus == OrderStatus.待付款 && x.UserId == UserId).First();
        if (order != null)
        {
            InlineKeyboardMarkup inlineKeyboard = new(
            new[]
            {
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("立即支付",$"PayOrder|{order.Id}"),
                            InlineKeyboardButton.WithCallbackData("取消订单",$"CancelOrder|{order.Id}"),
                        },
                        new[]
                        {
                            InlineKeyboardButton.WithUrl("联系客服",AdminUserUrl)
                        }
            });
            return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                    text: @$"<b>您还有未支付订单！</b>

订单号：<code>{order.Id}</code>
下单用户：<code>{order.UserId}</code>
下单时间：<code>{order.CreateTime:yyyy-MM-dd HH:mm}</code>
订单金额：<b>{order.CNY}</b> 元 / <b>{order.USDT}</b> USDT
TG用户名：<b>{order.AccountInfo}</b>
开通时长：<b>{order.Months}</b>个月Telegram Premium会员
订单备注：<b>{(string.IsNullOrEmpty(order.Memo) ? "无" : order.Memo)}</b>

🟢如需取消此订单的支付，请点击取消订单！
", parseMode: ParseMode.Html,
                                                    replyMarkup: inlineKeyboard);
        }

        dic.TryAdd(UserId, string.Empty);
        InlineKeyboardMarkup _inlineKeyboard = new(
                new[]
                {
                        new[]
                        {
                            InlineKeyboardButton.WithUrl("联系客服",AdminUserUrl)
                        }
                });
        return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                    text: @$"<b>请输入您Telegram用户名，以@开头：</b>", parseMode: ParseMode.Html);
    }
    private static async Task BotOnCallbackQueryReceived(ITelegramBotClient botClient, CallbackQuery callbackQuery)
    {
        if (callbackQuery.Message == null) return;
        var data = callbackQuery.Data ?? "";
        var UserId = callbackQuery.Message.ToUserId();
        var chatId = callbackQuery.Message.Chat.Id;
        var messageId = callbackQuery.Message.MessageId;
        var from = callbackQuery.From;

        Log.Information("{user}({username})[{id}]: {message}", $"{from.FirstName} {from.LastName}", "@" + from.Username, from.Id, data);

        if (data == "关闭")
        {
            await botClient.AnswerCallbackQueryAsync(
                callbackQueryId: callbackQuery.Id,
                text: $"已关闭");
            await DeleteMessageAsync(botClient, chatId, messageId);
            return;
        }
        else if ((data.StartsWith("PayOrder|") || data.StartsWith("ChangePayMethod|"))
            && long.TryParse(data.Replace("PayOrder|", "").Replace("ChangePayMethod|", ""), out var PayOrderId))
        {
            var curd = freeSql.GetRepository<Orders>();
            var order = await curd.Where(x => x.Id == PayOrderId && x.UserId == UserId).FirstAsync();
            if (order == null)
            {
                await botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: $"订单不存在或已被取消！");
                await DeleteMessageAsync(botClient, chatId, messageId);
                return;
            }
            if (order.OrderStatus != OrderStatus.待付款)
            {
                await botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: $"订单已支付或已取消！");
                await DeleteMessageAsync(botClient, chatId, messageId);
                return;
            }
            var pays = new List<InlineKeyboardButton>();
            var usdt = configuration.GetValue<string>($"USDTPayQrCode");
            if (usdt != null)
            {
                pays.Add(InlineKeyboardButton.WithCallbackData("USDT支付", $"USDTPay|{order.Id}"));
            }
            var ali = configuration.GetValue<string>($"AliPayQrCode");
            if (ali != null)
            {
                pays.Add(InlineKeyboardButton.WithCallbackData("支付宝支付", $"AliPay|{order.Id}"));
            }
            var wx = configuration.GetValue<string>($"WeChatPayQrCode");
            if (wx != null)
            {
                pays.Add(InlineKeyboardButton.WithCallbackData("微信支付", $"WechatPay|{order.Id}"));
            }
            if (pays.Count == 0)
            {
                pays.Add(InlineKeyboardButton.WithCallbackData("🔴暂无可用支付方式"));
            }
            InlineKeyboardMarkup inlineKeyboard = new(
                new[]
                {
                        pays.ToArray(),
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("刷新", $"PayOrder|{order.Id}"),
                            InlineKeyboardButton.WithCallbackData("取消订单",$"CancelOrder|{order.Id}"),
                        },
                        new[]
                        {
                            InlineKeyboardButton.WithUrl("联系客服",AdminUserUrl)
                        }
                });
            var memo = @$"
🔴<b>此订单暂仅支持支付宝付款</b>
";
            if (order.CNY < 500)
            {
                memo = "";
            }
            var text = @$"当前时间:{DateTime.Now:yyyy-MM-dd HH:mm:ss}

订单号：<code>{order.Id}</code>
下单用户：<code>{order.UserId}</code>
下单时间：<code>{order.CreateTime:yyyy-MM-dd HH:mm}</code>
订单金额：<b>{order.CNY}</b> 元 / <b>{order.USDT}</b> USDT
TG用户名：<b>{order.AccountInfo}</b>
开通时长：<b>{order.Months}</b>个月Telegram Premium会员
订单备注：<b>{(string.IsNullOrEmpty(order.Memo) ? "无" : order.Memo)}</b>
{memo}
<b>请选择支付方式：</b>
";
            if (data.StartsWith("ChangePayMethod|"))
            {
                await DeleteMessageAsync(botClient, chatId, messageId);
                await botClient.SendTextMessageAsync(chatId, text, ParseMode.Html, replyMarkup: inlineKeyboard);
            }
            else
            {
                await EditMessageTextAsync(botClient, chatId, messageId, text, inlineKeyboard);
            }
            return;
        }
        else if (data.StartsWith("CancelOrder|") && long.TryParse(data.Replace("CancelOrder|", ""), out var CancelOrderId))
        {
            var curd = freeSql.GetRepository<Orders>();
            var order = await curd.Where(x => x.Id == CancelOrderId && x.UserId == UserId).FirstAsync();
            if (order == null)
            {
                await botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: $"订单不存在或已被取消！");
                await DeleteMessageAsync(botClient, chatId, messageId);
                return;
            }
            if (order.OrderStatus != OrderStatus.待付款)
            {
                await botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: $"订单已支付或已取消！");
                await DeleteMessageAsync(botClient, chatId, messageId);
                return;
            }
            order.IsDeleted = true;
            order.DeletedTime = DateTime.Now;
            await curd.UpdateAsync(order);
            await botClient.AnswerCallbackQueryAsync(
                callbackQueryId: callbackQuery.Id,
                text: $"您的订单已成功取消！");
            await DeleteMessageAsync(botClient, chatId, messageId);
            return;
        }
        else if (data.StartsWith("USDTPay|") && long.TryParse(data.Replace("USDTPay|", ""), out var USDTPayOrderId))
        {
            var curd = freeSql.GetRepository<Orders>();
            var order = await curd.Where(x => x.Id == USDTPayOrderId && x.UserId == UserId).FirstAsync();
            if (order == null)
            {
                await botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: $"订单不存在或已被取消！");
                await DeleteMessageAsync(botClient, chatId, messageId);
                return;
            }
            if (order.OrderStatus != OrderStatus.待付款)
            {
                await botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: $"订单已支付或已取消！");
                await DeleteMessageAsync(botClient, chatId, messageId);
                return;
            }
            order.PayMethod = PayMethod.USDT;
            await curd.UpdateAsync(order);
            var code = configuration.GetValue($"USDTPayQrCode", "");
            InlineKeyboardMarkup inlineKeyboard = new(
                new[]
                {
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("我已支付",$"PayDone|{order.Id}"),
                            InlineKeyboardButton.WithCallbackData("取消订单",$"CancelOrder|{order.Id}"),
                        },
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("刷新付款码",$"USDTPay|{order.Id}"),
                            InlineKeyboardButton.WithCallbackData("更换支付方式",$"ChangePayMethod|{order.Id}"),
                        },
                        new[]
                        {
                            InlineKeyboardButton.WithUrl("联系客服",AdminUserUrl)
                        }
                });
            var bytes = CreateQrCode(code, "Resrouces/usdt.png", order);
            var imgText = $@"订单号：<code>{order.Id}</code>
下单用户：<code>{order.UserId}</code>
下单时间：<code>{order.CreateTime:yyyy-MM-dd HH:mm}</code>
订单金额：<b>{order.USDT}</b> USDT
TG用户名：<b>{order.AccountInfo}</b>
开通时长：<b>{order.Months}</b>个月Telegram Premium会员
订单备注：<b>{(string.IsNullOrEmpty(order.Memo) ? "无" : order.Memo)}</b>

🟢请使用【支持{order.PayMethod}-TRC20的钱包】，扫描上方支付二维码
🟢也可点击复制下方付款地址
付款地址：<code>{code}</code>

🔴<b>支付完成后，请点击下方【我已支付】</b>

<b>已选择的支付方式：{order.PayMethod}支付</b>
<b>您需要支付的金额：{order.USDT} USDT</b>";
            await botClient.SendPhotoAsync(chatId: chatId,
                                                    new InputOnlineFile(new MemoryStream(bytes)),
                                                    imgText,
                                                    parseMode: ParseMode.Html,
                                                    replyMarkup: inlineKeyboard);
            await DeleteMessageAsync(botClient, chatId, messageId);
            return;
        }
        else if (data.StartsWith("WechatPay|") && long.TryParse(data.Replace("WechatPay|", ""), out var WeChatPayOrderId))
        {
            var curd = freeSql.GetRepository<Orders>();
            var order = await curd.Where(x => x.Id == WeChatPayOrderId && x.UserId == UserId).FirstAsync();
            if (order == null)
            {
                await botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: $"订单不存在或已被取消！");
                await DeleteMessageAsync(botClient, chatId, messageId);
                return;
            }
            if (order.OrderStatus != OrderStatus.待付款)
            {
                await botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: $"订单已支付或已取消！");
                await DeleteMessageAsync(botClient, chatId, messageId);
                return;
            }
            order.PayMethod = PayMethod.微信;
            await curd.UpdateAsync(order);


            InlineKeyboardMarkup inlineKeyboard = new(
                new[]
                {
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("我已支付",$"PayDone|{order.Id}"),
                            InlineKeyboardButton.WithCallbackData("取消订单",$"CancelOrder|{order.Id}"),
                        },
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("刷新付款码",$"WechatPay|{order.Id}"),
                            InlineKeyboardButton.WithCallbackData("更换支付方式",$"ChangePayMethod|{order.Id}"),
                        },
                        new[]
                        {
                            InlineKeyboardButton.WithUrl("联系客服",AdminUserUrl)
                        }
                });
            var code = configuration.GetValue($"WeChatPayQrCode", $"{order.CNY} CNY") ?? "";
            var bytes = CreateQrCode(code, "Resrouces/wechatpay.png", order);
            var imgText = $@"订单号：<code>{order.Id}</code>
下单用户：<code>{order.UserId}</code>
下单时间：<code>{order.CreateTime:yyyy-MM-dd HH:mm}</code>
订单金额：<b>{order.CNY}</b> 元
TG用户名：<b>{order.AccountInfo}</b>
开通时长：<b>{order.Months}</b>个月Telegram Premium会员
订单备注：<b>{(string.IsNullOrEmpty(order.Memo) ? "无" : order.Memo)}</b>

🟢请使用【{order.PayMethod}扫一扫】，扫描上方支付二维码
🟢手机用户可截图此付款码，并打开【{order.PayMethod}】识别二维码

🔴<b>支付完成后，请点击下方【我已支付】</b>

<b>已选择的支付方式：{order.PayMethod}支付</b>
<b>您需要支付的金额：{order.CNY} CNY</b>";
            await botClient.SendPhotoAsync(chatId: chatId,
                                                    new InputOnlineFile(new MemoryStream(bytes)),
                                                    imgText,
                                                    parseMode: ParseMode.Html,
                                                    replyMarkup: inlineKeyboard);
            await DeleteMessageAsync(botClient, chatId, messageId);
            return;
        }
        else if (data.StartsWith("AliPay|") && long.TryParse(data.Replace("AliPay|", ""), out var AliPayOrderId))
        {
            var curd = freeSql.GetRepository<Orders>();
            var order = await curd.Where(x => x.Id == AliPayOrderId && x.UserId == UserId).FirstAsync();
            if (order == null)
            {
                await botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: $"订单不存在或已被取消！");
                await DeleteMessageAsync(botClient, chatId, messageId);
                return;
            }
            if (order.OrderStatus != OrderStatus.待付款)
            {
                await botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: $"订单已支付或已取消！");
                await DeleteMessageAsync(botClient, chatId, messageId);
                return;
            }
            order.PayMethod = PayMethod.支付宝;
            await curd.UpdateAsync(order);
            var code = configuration.GetValue($"AliPayQrCode", "");
            InlineKeyboardMarkup inlineKeyboard = new(
                new[]
                {
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("我已支付",$"PayDone|{order.Id}"),
                            InlineKeyboardButton.WithCallbackData("取消订单",$"CancelOrder|{order.Id}"),
                        },
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("刷新付款码",$"AliPay|{order.Id}"),
                            InlineKeyboardButton.WithCallbackData("更换支付方式",$"ChangePayMethod|{order.Id}"),
                        },
                        new[]
                        {
                            InlineKeyboardButton.WithUrl("联系客服",AdminUserUrl)
                        }
                });
            var bytes = CreateQrCode(code, "Resrouces/alipay.png", order);
            var imgText = $@"订单号：<code>{order.Id}</code>
下单用户：<code>{order.UserId}</code>
下单时间：<code>{order.CreateTime:yyyy-MM-dd HH:mm}</code>
订单金额：<b>{order.CNY}</b> 元
TG用户名：<b>{order.AccountInfo}</b>
开通时长：<b>{order.Months}</b>个月Telegram Premium会员
订单备注：<b>{(string.IsNullOrEmpty(order.Memo) ? "无" : order.Memo)}</b>

🟢请使用【{order.PayMethod}扫一扫】，扫描上方支付二维码
🟢手机用户可截图此付款码，并打开【{order.PayMethod}】识别二维码

🔴<b>支付完成后，请点击下方【我已支付】</b>

<b>已选择的支付方式：{order.PayMethod}支付</b>
<b>您需要支付的金额：{order.CNY} CNY</b>";
            await botClient.SendPhotoAsync(chatId: chatId,
                                                    new InputOnlineFile(new MemoryStream(bytes)),
                                                    imgText,
                                                    parseMode: ParseMode.Html,
                                                    replyMarkup: inlineKeyboard);
            await DeleteMessageAsync(botClient, chatId, messageId);
            return;
        }
        else if (data.StartsWith("PayDone|") && long.TryParse(data.Replace("PayDone|", ""), out var PayDoneOrderId))
        {
            var curd = freeSql.GetRepository<Orders>();
            var order = await curd.Where(x => x.Id == PayDoneOrderId && x.UserId == UserId).FirstAsync();
            if (order == null)
            {
                await botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: $"订单不存在或已被取消！");
                await DeleteMessageAsync(botClient, chatId, messageId);
                return;
            }
            if (order.OrderStatus != OrderStatus.待付款)
            {
                await botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: $"订单已支付或已取消！");
                await DeleteMessageAsync(botClient, chatId, messageId);
                return;
            }
            var action = "AliPay";
            if (order.PayMethod == PayMethod.微信)
            {
                action = "WechatPay";
            }
            else if (order.PayMethod == PayMethod.USDT)
            {
                action = "USDTPay";
            }
            InlineKeyboardMarkup inlineKeyboard = new(
                new[]
                {
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("我要重新支付",$"{action}|{order.Id}"),
                        },
                        new[]
                        {
                            InlineKeyboardButton.WithUrl("联系客服",AdminUserUrl)
                        }
                });
            var text = $"<b>请输入转账金额为【{order.CNY}CNY】的";
            if (order.PayMethod == PayMethod.支付宝)
            {
                text += @"支付宝【订单号】</b>

🔴点击支付宝支付记录，长按【订单号】，并复制【订单号】

<code>支付宝支付记录可在【支付宝App】-->【我的】-->【账单】中找到</code>
";
            }
            else if (order.PayMethod == PayMethod.微信)
            {
                text += @"微信【转账单号】</b>

🔴点击微信支付记录，长按【转账单号】，并复制【转账单号】

<code>微信支付记录可在【微信App】-->【我】-->【服务（或支付）】-->【钱包】-->右上角【账单】中找到</code>
";
            }
            else if (order.PayMethod == PayMethod.USDT)
            {
                text = @$"<b>请输入转账金额为【{order.USDT}USDT】的转账【交易哈希】</b>";
            }
            else
            {
                text += "支付流水号";
            }
            await botClient.SendTextMessageAsync(chatId, text, ParseMode.Html, replyMarkup: inlineKeyboard);
            await DeleteMessageAsync(botClient, chatId, messageId);
            dicTradeNo.TryRemove(UserId, out var _);
            dicTradeNo.TryAdd(UserId, order.Id);
            return;
        }
        else if (data.StartsWith("AdminPayDone|") && long.TryParse(data.Replace("AdminPayDone|", ""), out var AdminPayDoneOrderId))
        {
            if (UserId != AdminUserId) return;
            var curd = freeSql.GetRepository<Orders>();
            var order = await curd.Where(x => x.Id == AdminPayDoneOrderId).FirstAsync();
            if (order == null)
            {
                await botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: $"订单不存在或已被取消！");
                await DeleteMessageAsync(botClient, chatId, messageId);
                return;
            }
            InlineKeyboardMarkup adminKeyboard = new(
                new[]
                {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("重设为拒绝",$"AdminPayCancel|{order.Id}"),
                        }
                });
            if (order.OrderStatus == OrderStatus.完成)
            {
                await botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: $"订单已完成，请勿重复操作！");
                await botClient.EditMessageReplyMarkupAsync(chatId, messageId, adminKeyboard);
                return;
            }
            order.OrderStatus = OrderStatus.完成;
            order.EndTime = DateTime.Now;
            await curd.UpdateAsync(order);
            var senText = $@"<b>订单完成通知！</b>

订单号：<code>{order.Id}</code>
下单用户：<code>{order.UserId}</code>
下单时间：<code>{order.CreateTime:yyyy-MM-dd HH:mm}</code>
订单金额：<b>{order.CNY}</b> 元 / <b>{order.USDT}</b> USDT
TG用户名：<b>{order.AccountInfo}</b>
开通时长：<b>{order.Months}</b>个月Telegram Premium会员

支付方式：<b>{order.PayMethod}</b>
支付时间：<b>{order.PayTime:yyyy-MM-dd HH:mm}</b>
支付单号：<code>{order.TradeNo}</code>

订单备注：<b>{(string.IsNullOrEmpty(order.Memo) ? "无" : order.Memo)}</b>

<b>请检查您的Telegram Premium订阅情况！</b>
";
            InlineKeyboardMarkup inlineKeyboard = new(
                new[]
                {
                        new[]
                        {
                            InlineKeyboardButton.WithUrl("联系客服",AdminUserUrl)
                        }
                });
            await botClient.SendTextMessageAsync(order.UserId, senText, ParseMode.Html, replyMarkup: inlineKeyboard);
            await botClient.EditMessageReplyMarkupAsync(chatId, messageId, adminKeyboard);
            return;
        }
        else if (data.StartsWith("AdminPayCancel|") && long.TryParse(data.Replace("AdminPayCancel|", ""), out var AdminPayCancelOrderId))
        {
            if (UserId != AdminUserId) return;
            var curd = freeSql.GetRepository<Orders>();
            var order = await curd.Where(x => x.Id == AdminPayCancelOrderId).FirstAsync();
            if (order == null)
            {
                await botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: $"订单不存在或已被取消！");
                await DeleteMessageAsync(botClient, chatId, messageId);
                return;
            }
            if (string.IsNullOrEmpty(order.FailMemo))
            {
                await botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: $"未填写拒绝原因！");
                return;
            }
            InlineKeyboardMarkup adminKeyboard = new(
                new[]
                {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("重设为完成",$"AdminPayDone|{order.Id}"),
                        }
                });
            if (order.OrderStatus == OrderStatus.拒绝)
            {
                await botClient.AnswerCallbackQueryAsync(
                    callbackQueryId: callbackQuery.Id,
                    text: $"订单已拒绝，请勿重复操作！");
                await botClient.EditMessageReplyMarkupAsync(chatId, messageId, adminKeyboard);
                return;
            }
            order.OrderStatus = OrderStatus.拒绝;
            order.EndTime = DateTime.Now;
            await curd.UpdateAsync(order);
            var senText = $@"<b>您的订单被拒绝！</b>

订单号：<code>{order.Id}</code>
下单用户：<code>{order.UserId}</code>
下单时间：<code>{order.CreateTime:yyyy-MM-dd HH:mm}</code>
订单金额：<b>{order.CNY}</b> 元 / <b>{order.USDT}</b> USDT
TG用户名：<b>{order.AccountInfo}</b>
开通时长：<b>{order.Months}</b>个月Telegram Premium会员

支付方式：<b>{order.PayMethod}</b>
支付时间：<b>{order.PayTime:yyyy-MM-dd HH:mm}</b>
支付单号：<code>{order.TradeNo}</code>

拒绝原因：<b>{(string.IsNullOrEmpty(order.FailMemo) ? "无" : order.FailMemo)}</b>

<b>如有疑问，请联系客服！</b>
";
            InlineKeyboardMarkup inlineKeyboard = new(
                new[]
                {
                        new[]
                        {
                            InlineKeyboardButton.WithUrl("联系客服",AdminUserUrl)
                        }
                });
            await botClient.SendTextMessageAsync(order.UserId, senText, ParseMode.Html, replyMarkup: inlineKeyboard);

            await botClient.EditMessageReplyMarkupAsync(chatId, messageId, adminKeyboard);
            return;
        }
        else if (data.StartsWith("ViewOrder|") && long.TryParse(data.Replace("ViewOrder|", ""), out var ViewOrderOrderId))
        {
            if (AdminUserId == UserId)
            {
                var curd = freeSql.GetRepository<Orders>();
                var item = await curd.Where(x => x.Id == ViewOrderOrderId).Include(x => x.User).FirstAsync();
                if (item != null)
                {
                    var orderText = @$"下单用户：<code>{item.UserId}</code>
下单用户：<b>{(string.IsNullOrEmpty(item.User?.UserName) ? "" : "@")}{item.User?.UserName}</b>
下单用户：<code>{item.User?.FirstName} {item.User?.LastName}</code>
下单时间：<code>{item.CreateTime:yyyy-MM-dd HH:mm}</code>
<a href=""tg://user?id={item.UserId}"">查看此用户</a>

订单号: <code>{item.Id}</code>
订单状态：<b>{item.OrderStatus}</b>
订单金额：<b>{item.CNY}</b> 元 / <b>{item.USDT}</b> USDT
TG用户名：<b>{item.AccountInfo}</b>
开通时长：<b>{item.Months}</b>个月Telegram Premium会员

支付方式：<b>{item.PayMethod}</b>
支付时间：<b>{item.PayTime:yyyy-MM-dd HH:mm:ss}</b>
支付单号：<code>{item.TradeNo}</code>

订单备注：<b>{(string.IsNullOrEmpty(item.Memo) ? "无" : item.Memo)}</b>
拒绝原因：<b>{(string.IsNullOrEmpty(item.FailMemo) ? "无" : item.FailMemo)}</b>
";

                    InlineKeyboardMarkup viewOrder = new(
                        new[]
                        {
                                new[]
                                {
                                    InlineKeyboardButton.WithCallbackData("关闭"),
                                }
                        });
                    await botClient.SendTextMessageAsync(chatId: chatId,
                                                                text: orderText, parseMode: ParseMode.Html,
                                                                replyMarkup: viewOrder);
                }
                else
                {

                    await botClient.SendTextMessageAsync(chatId: chatId,
                                                                text: "订单不存在或已删除！");
                }
            }
            return;
        }
        else if (data.StartsWith("提示|"))
        {
            var text = data.Replace("提示|", "");
            await botClient.AnswerCallbackQueryAsync(
                callbackQueryId: callbackQuery.Id,
                text: text);
            return;
        }
    }
    static async Task<Message> EditMessageTextAsync(ITelegramBotClient botClient, long ChatId, int MessageId, string Text, InlineKeyboardMarkup? inlineKeyboard = null)
    {
        try
        {
            return await botClient.EditMessageTextAsync(ChatId, MessageId, Text, ParseMode.Html, replyMarkup: inlineKeyboard);
        }
        catch (Exception)
        {
            Log.Information("编辑消息失败！ChatID:{a}, MsgId:{b}", ChatId, MessageId);
        }
        return new Message();
    }
    static async Task DeleteMessageAsync(ITelegramBotClient botClient, long ChatId, int MessageId)
    {
        try
        {
            await botClient.DeleteMessageAsync(ChatId, MessageId);
        }
        catch (Exception)
        {
            Log.Information("删除消息失败！ChatID:{a}, MsgId:{b}", ChatId, MessageId);
        }
    }
    private static async Task InsertOrUpdateUserAsync(ITelegramBotClient botClient, Message message)
    {
        if (message.From == null) return;
        var curd = ServiceProvider.GetRequiredService<IBaseRepository<Users>>();
        var from = message.From;
        var UserId = message.Chat.Id;
        if (UserId < 0) return;
        Log.Information("{user}({username})[{id}]: {message}", $"{from.FirstName} {from.LastName}", "@" + from.Username, from.Id, message.Text);

        var user = await curd.Where(x => x.UserId == UserId).FirstAsync();
        if (user == null)
        {
            user = new Users
            {
                UserId = UserId,
                UserName = from.Username,
                FirstName = from.FirstName,
                LastName = from.LastName,
                CreateTime = DateTime.Now,
            };
            await curd.InsertAsync(user);
            return;
        }
        user.UserId = UserId;
        user.UserName = from.Username;
        user.FirstName = from.FirstName;
        user.LastName = from.LastName;
        user.UpdateTime = DateTime.Now;
        await curd.UpdateAsync(user);
    }
    /// <summary>
    /// 创建二维码
    /// </summary>
    /// <param name="qrcode"></param>
    /// <returns></returns>
    public static byte[] CreateQrCode(string qrcode, string? logoPath = null, Orders? order = null)
    {
        using var stream = new MemoryStream();
        using var generator = new QRCodeGenerator();
        var qr = generator.CreateQrCode(qrcode, ECCLevel.H, quietZoneSize: 2);
        var info = new SKImageInfo(250, 250);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        if (logoPath != null)
        {
            var logo = System.IO.File.ReadAllBytes(logoPath);
            var icon = new IconData
            {
                Icon = SKBitmap.Decode(logo),
                IconSizePercent = 20,
            };
            canvas.Render(qr, info.Width, info.Height, SKColor.Empty, SKColor.Parse("000000"), icon);
        }
        else
        {
            canvas.Render(qr, info.Width, info.Height);
        }
        if (order != null)
        {
            var font = SKTypeface.FromFile("Resrouces/AlibabaPuHuiTi-2-85-Bold.ttf");

            var brush = new SKPaint
            {
                Typeface = font,
                TextSize = 12.0f,
                Color = SKColors.Red,
                TextAlign = SKTextAlign.Center,
                FilterQuality = SKFilterQuality.High,
            };
            if (order.PayMethod == PayMethod.USDT)
            {
                canvas.DrawText($"订单号：{order.Id}    支付金额：{order.USDT} USDT", info.Width / 2, info.Height - brush.TextSize / 2 + 5, brush);
            }
            else
            {
                canvas.DrawText($"订单号：{order.Id}    支付金额：{order.CNY} CNY", info.Width / 2, info.Height - brush.TextSize / 2, brush);
            }
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        data.SaveTo(stream);
        return stream.ToArray();
    }
    private static long ToUserId(this Message message)
    {
        return message.Chat.Id;
    }
    private static async Task<Users> ToUser(this Message message, bool IsUpdate = false, IRepositoryUnitOfWork? uow = null)
    {
        var UserId = message.ToUserId();
        var _userRepository = uow == null ? freeSql.GetRepository<Users>() : uow.GetRepository<Users>();
        var query = _userRepository.Where(x => x.UserId == UserId);
        if (IsUpdate)
        {
            query = query.ForUpdate();
        }
        var user = await query.FirstAsync();
        return user;
    }
}