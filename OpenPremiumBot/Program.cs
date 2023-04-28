using FreeSql;
using FreeSql.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenPremiumBot.BgServices;
using OpenPremiumBot.Domains;
using Serilog;
using System.Net;
using System.Reflection;
using Telegram.Bot;
using Telegram.Bot.Types;


Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

var host = Host.CreateDefaultBuilder(args);

host.UseSerilog((context, services, configuration) => configuration
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext()
                    .WriteTo.Console());
host.ConfigureServices(ConfigureServices);
using var app = host.Build();

try
{
    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

static void ConfigureServices(HostBuilderContext Context, IServiceCollection Services)
{
    var Configuration = Context.Configuration;
    var HostingEnvironment = Context.HostingEnvironment;

    #region 数据库
    var connectionString = Configuration.GetConnectionString("DB");

    IFreeSql fsql = new FreeSqlBuilder()
        .UseConnectionString(FreeSql.DataType.Sqlite, connectionString)
        .UseAutoSyncStructure(true) //自动同步实体结构
        .Build();

    //启动时同步表结构
    fsql.CodeFirst.SyncStructure(GetTypesByNameSpace());
    fsql.GlobalFilter.Apply<ISoftDelete>("SoftDelete", a => a.IsDeleted == false);
    Services.AddSingleton(fsql);
    Services.AddScoped<UnitOfWorkManager>();
    Services.AddFreeRepository();

    #endregion

    #region 机器人
    var token = Configuration.GetValue<string>("BotConfig:Token")!;
    var baseUrl = Configuration.GetValue<string>("BotConfig:Proxy");
    var useProxy = Configuration.GetValue<bool>("BotConfig:UseProxy");
    TelegramBotClient botClient = new TelegramBotClient(new TelegramBotClientOptions(token, baseUrl));

    var WebProxy = Configuration.GetValue<string>("WebProxy");
    if (useProxy && !string.IsNullOrEmpty(WebProxy))
    {
        var uri = new Uri(WebProxy);
        var userinfo = uri.UserInfo.Split(":");
        var webProxy = new WebProxy($"{uri.Scheme}://{uri.Authority}")
        {
            Credentials = string.IsNullOrEmpty(uri.UserInfo) ? null : new NetworkCredential(userinfo[0], userinfo[1])
        };
        var httpClient = new HttpClient(
            new HttpClientHandler { Proxy = webProxy, UseProxy = true, }
        );
        botClient = new TelegramBotClient(token, httpClient);

    }
    Log.Logger.Information("开始{UseProxy}连接Telegram服务器...", (useProxy ? "使用代理" : "不使用代理"));
    var me = botClient.GetMeAsync().GetAwaiter().GetResult();
    botClient.SetMyCommandsAsync(new BotCommand[]
    {
        new BotCommand(){Command="start",Description="开始下单"}
    }).GetAwaiter().GetResult();
    Log.Logger.Information("Telegram机器人上线！机器人ID：{Id}，机器人名字：{FirstName}.", me.Id, me.FirstName);
    Services.AddSingleton<ITelegramBotClient>(botClient);
    var AdminUserId = Configuration.GetValue<long>("BotConfig:AdminUserId");
    if (AdminUserId != 0)
    {
        botClient.SendTextMessageAsync(AdminUserId, $"您的机器人<a href=\"tg://user?id={me.Id}\">{me.FirstName}</a>已上线!", Telegram.Bot.Types.Enums.ParseMode.Html);
    }
    Services.AddHostedService<BotService>();
    Services.AddHostedService<AutoCancelOrderService>();
    #endregion

}


static Type[] GetTypesByNameSpace()
{
    List<Type> tableAssembies = new List<Type>();
    List<string> entitiesFullName = new List<string>()
    {
        typeof(IEntity).Namespace??"Domain"
    };
    foreach (Type type in Assembly.GetAssembly(typeof(IEntity))!.GetExportedTypes())
        foreach (var fullname in entitiesFullName)
            if (type.FullName!.StartsWith(fullname) && type.IsClass && type.GetCustomAttributes().Any(
                x => x is not TableAttribute || x is TableAttribute && !((TableAttribute)x).DisableSyncStructure))
                tableAssembies.Add(type);

    return tableAssembies.ToArray();
}
