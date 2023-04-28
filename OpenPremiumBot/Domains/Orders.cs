using FreeSql.DataAnnotations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenPremiumBot.Domains
{
    public class Orders : IEntity
    {
        [Column(IsPrimary = true, IsIdentity = true)]
        public long Id { get; set; }
        /// <summary>
        /// 下单用户
        /// </summary>
        public long UserId { get; set; }
        /// <summary>
        /// 用户
        /// </summary>
        public Users User { get; set; }
        /// <summary>
        /// 账号信息
        /// </summary>
        public string AccountInfo { get; set; }
        /// <summary>
        /// 开通时长
        /// </summary>
        public int Months { get; set; }
        /// <summary>
        /// 人民币金额
        /// </summary>
        public decimal CNY { get; set; }
        /// <summary>
        /// USDT金额
        /// </summary>
        public decimal USDT { get; set; }
        /// <summary>
        /// 转账单号
        /// </summary>
        public string? TradeNo { get; set; }
        /// <summary>
        /// 支付时间
        /// </summary>
        public DateTime? PayTime { get; set; }
        /// <summary>
        /// 付款方式
        /// </summary>
        public PayMethod PayMethod { get; set; }
        /// <summary>
        /// 订单状态
        /// </summary>
        public OrderStatus OrderStatus { get; set; }
        /// <summary>
        /// 结单时间
        /// </summary>
        public DateTime? EndTime { get; set; }
        /// <summary>
        /// 发货备注
        /// </summary>
        public string? Memo { get; set; }
        /// <summary>
        /// 失败备注
        /// </summary>
        public string? FailMemo { get; set; }
        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreateTime { get; set; }
        public bool IsDeleted { get; set; }
        public DateTime? DeletedTime { get; set; }
    }
    public enum PayMethod
    {
        Unknown = 0,
        支付宝,
        微信,
        USDT
    }
    public enum OrderStatus
    {
        Unknown = 0,
        待付款,
        待处理,
        拒绝,
        完成
    }
}
