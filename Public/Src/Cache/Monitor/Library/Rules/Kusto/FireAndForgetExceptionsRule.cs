﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using BuildXL.Cache.Monitor.App.Scheduling;
using BuildXL.Cache.Monitor.Library.Rules;
using Kusto.Data.Common;
using static BuildXL.Cache.Monitor.App.Analysis.Utilities;

namespace BuildXL.Cache.Monitor.App.Rules.Kusto
{
    internal class FireAndForgetExceptionsRule : MultipleStampRuleBase
    {
        public class Configuration : MultiStampRuleConfiguration
        {
            public Configuration(MultiStampRuleConfiguration kustoRuleConfiguration)
                : base(kustoRuleConfiguration)
            {
            }

            public TimeSpan LookbackPeriod { get; set; } = TimeSpan.FromMinutes(30);

            public Thresholds<long> MachinesThresholds = new Thresholds<long>()
            {
                Warning = 10,
                Error = 20,
            };

            public int MinimumErrorsThreshold { get; set; } = 20;
        }

        private readonly Configuration _configuration;

        /// <inheritdoc />
        public override string Identifier => $"{nameof(FireAndForgetExceptionsRule)}:{_configuration.Environment}";

        public FireAndForgetExceptionsRule(Configuration configuration)
            : base(configuration)
        {
            _configuration = configuration;
        }

#pragma warning disable CS0649
        internal class Result
        {
            public string Operation = string.Empty;
            public long Machines;
            public long Count;
            public string Stamp = string.Empty;
        }
#pragma warning restore CS0649

        public override async Task Run(RuleContext context)
        {
            // NOTE(jubayard): When a summarize is run over an empty result set, Kusto produces a single (null) row,
            // which is why we need to filter it out.
            var now = _configuration.Clock.UtcNow;
            var query =
                $@"
                let end = now();
                let start = end - {CslTimeSpanLiteral.AsCslString(_configuration.LookbackPeriod)};
                table('{_configuration.CacheTableName}')
                | where PreciseTimeStamp between (start .. end)
                | where Message has 'Unhandled exception in fire and forget task'
                | where Message !has 'RedisConnectionException' // This is a transient error (i.e. server closed the socket)
                | where Message !has 'TaskCanceledException' // This is irrelevant
                | parse Message with * 'operation '' Operation:string ''' * 'FullException=' Exception:string
                | project PreciseTimeStamp, Machine, Operation, Exception, Stamp
                | summarize Machines=dcount(Machine), Count=count() by Operation, Stamp
                | where not(isnull(Machines))";
            var results = await QueryKustoAsync<Result>(context, query);

            foreach (var result in results)
            {
                _configuration.MachinesThresholds.Check(result.Machines, (severity, threshold) =>
                {
                    if (result.Count < _configuration.MinimumErrorsThreshold)
                    {
                        return;
                    }

                    Emit(context, $"FireAndForgetExceptions_Operation_{result.Operation}", severity,
                        $"`{result.Machines}` machines had `{result.Count}` errors in fire and forget tasks for operation `{result.Operation}`",
                        result.Stamp,
                        eventTimeUtc: now);
                });
            }
        }
    }
}
