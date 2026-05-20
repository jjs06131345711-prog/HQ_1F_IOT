
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SANJET.Core.Interfaces;

namespace SANJET.Core.Services
{
    public class CompositeLineNotificationService : ILineNotificationService
    {
        private readonly IReadOnlyList<ILineNotificationChannel> _channels;
        private readonly ILogger<CompositeLineNotificationService> _logger;

        public CompositeLineNotificationService(
            IEnumerable<ILineNotificationChannel> channels,
            ILogger<CompositeLineNotificationService> logger)
        {
            _channels = channels?.ToArray() ?? throw new ArgumentNullException(nameof(channels));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool IsConfigured => _channels.Any(channel => channel.IsConfigured);

        public async Task SendTextMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            var enabledChannels = _channels.Where(channel => channel.IsEnabled).ToArray();
            if (enabledChannels.Length == 0)
            {
                _logger.LogWarning("未啟用任何 LINE 通知通道，略過推播。訊息: {Message}", message);
                throw new InvalidOperationException("未啟用任何 LINE 通知通道，無法發送通知。");
            }

            var attemptedChannelCount = 0;
            var succeededChannelCount = 0;
            var exceptions = new List<Exception>();

            foreach (var channel in enabledChannels)
            {
                if (!channel.IsConfigured)
                {
                    _logger.LogWarning("LINE 通知通道 {ChannelName} 已啟用但設定不完整，略過本通道。", channel.ChannelName);
                    continue;
                }

                attemptedChannelCount++;

                try
                {
                    await channel.SendTextMessageAsync(message, cancellationToken);
                    succeededChannelCount++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                    _logger.LogError(ex, "LINE 通知通道 {ChannelName} 發送失敗。", channel.ChannelName);
                }
            }

            if (succeededChannelCount == 0)
            {
                if (attemptedChannelCount == 0)
                {
                    throw new InvalidOperationException("已啟用的 LINE 通知通道皆未完成設定，無法發送通知。");
                }

                throw new AggregateException("所有已設定的 LINE 通知通道皆發送失敗。", exceptions);
            }
        }
    }
}
