using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using csv_prometheus_exporter.Parser;
using csv_prometheus_exporter.Prometheus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NLog;

namespace csv_prometheus_exporter.Scraper
{
    public class Startup
    {
        private const string PrometheusContentType = "text/plain; version=0.0.4; charset=utf-8";

        private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

        public static readonly IDictionary<string, SSHLogScraper> Scrapers =
            new ConcurrentDictionary<string, SSHLogScraper>();

        public static readonly IDictionary<string, MetricBase> Metrics = new Dictionary<string, MetricBase>();

        // This method gets called by the runtime. Use this method
        // to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddResponseCompression();

            services.AddResponseCompression(options =>
            {
                options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] {PrometheusContentType});
            });

            services.AddRouting();
        }

        private static Task Collect(HttpContext context)
        {
            return Task.Run(() => ExposeData(context));
        }

        private static readonly MetricBase ProcessCpuSecondsBase =
            new MetricBase("process_cpu_seconds", "Process CPU seconds", MetricsType.Counter);

        private static readonly MetricBase ProcessRssBytes =
            new MetricBase("process_resident_memory_bytes", "Process RSS", MetricsType.Gauge);

        private static readonly MetricBase ProcessStartTime = new MetricBase("process_start_time_seconds",
            "Process Start Time (Unix epoch)", MetricsType.Counter);

        private static readonly MetricBase ExposedMetrics =
            new MetricBase("exposed_metrics", "Currently exposed (active) metrics", MetricsType.Gauge);

        private static void ExposeProcessMetrics(StreamWriter textStream, int totalExposed)
        {
            var process = Process.GetCurrentProcess();

            ((Counter) ProcessCpuSecondsBase.WithLabels(new LabelDict(Environment.MachineName)))
                .Set(process.TotalProcessorTime.TotalSeconds);
            ProcessCpuSecondsBase.ExposeTo(textStream);

            ((Gauge) ProcessRssBytes.WithLabels(new LabelDict(Environment.MachineName)))
                .Set(process.WorkingSet64);
            ProcessRssBytes.ExposeTo(textStream);

            ((Counter) ProcessStartTime.WithLabels(new LabelDict(Environment.MachineName)))
                .Set(((DateTimeOffset) process.StartTime).ToUnixTimeSeconds());
            ProcessStartTime.ExposeTo(textStream);

            ((Gauge) ExposedMetrics.WithLabels(new LabelDict(Environment.MachineName)))
                .Set(totalExposed);
            ExposedMetrics.ExposeTo(textStream);
        }

        private static void ExposeData(HttpContext context)
        {
            var stopWatch = new Stopwatch();
            stopWatch.Start();
            // write the results
            context.Response.ContentType = PrometheusContentType;
            using (var textStream = new StreamWriter(context.Response.Body))
            {
                var totalExposed = 0;
                foreach (var aggregatedMetric in Metrics.Values)
                    totalExposed += aggregatedMetric.ExposeTo(textStream);

                totalExposed += MetricBase.Connected.ExposeTo(textStream);
                totalExposed += MetricBase.LinesParsed.ExposeTo(textStream);
                totalExposed += MetricBase.ParserErrors.ExposeTo(textStream);
                totalExposed += MetricBase.LinesParsedPerTarget.ExposeTo(textStream);
                totalExposed += MetricBase.ParserErrorsPerTarget.ExposeTo(textStream);
                totalExposed += MetricBase.SSHBytesIn.ExposeTo(textStream);

                ExposeProcessMetrics(textStream, totalExposed);
            }

            stopWatch.Stop();
            Logger.Info($"Write active metrics to response stream: {stopWatch.Elapsed}");
        }

        // This method gets called by the runtime. Use this method
        // to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app)
        {
            app.UseResponseCompression();

            var routeBuilder = new RouteBuilder(app);

            routeBuilder.MapGet("metrics", Collect);
            routeBuilder.MapGet("ping", context => context.Response.WriteAsync("pong"));

            app.UseRouter(routeBuilder.Build());
        }
    }
}