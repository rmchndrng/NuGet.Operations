﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NuGet.Services.Client;
using NuGet.Services.Work.Models;
using PowerArgs;

namespace NuCmd.Commands.Work
{
    [Description("Purges invocation records for invocations completed before the provided date")]
    public class PurgeCommand : WorkServiceCommandBase
    {
        [ArgShortcut("b")]
        [ArgPosition(0)]
        [ArgDescription("Purges invocations that completed before this UTC time")]
        public DateTime? BeforeUtc { get; set; }

        [ArgShortcut("bl")]
        [ArgPosition(0)]
        [ArgDescription("Purges invocations that completed before this local time")]
        public DateTime? BeforeLocal { get; set; }

        [ArgDescription("Purges a specific invocation")]
        public string Id { get; set; }

        protected override async Task OnExecute()
        {
            if (BeforeUtc == null && BeforeLocal != null)
            {
                BeforeUtc = BeforeLocal.Value.ToUniversalTime();
            }

            var client = await OpenClient();
            if (client == null) { return; }
            ServiceResponse<IEnumerable<Invocation>> response;
            if (!String.IsNullOrEmpty(Id))
            {
                await Console.WriteInfoLine("Purging invocation {0}...", Id);
            }
            else if (BeforeUtc == null)
            {
                await Console.WriteInfoLine(Strings.Work_PurgeCommand_PurgingAllInvocations);
            }
            else
            {
                await Console.WriteInfoLine(Strings.Work_PurgeCommand_PurgingInvocationsBefore, BeforeUtc.Value);
            }

            if (!String.IsNullOrEmpty(Id))
            {
                await ReportHttpStatus(await client.Invocations.Purge(Id));
            }
            else
            {
                if (WhatIf)
                {
                    // Get the list of invocations we would be able to purge
                    response = await client.Invocations.GetPurgable(BeforeUtc);
                }
                else
                {
                    // Get the list of invocations we would be able to purge
                    response = await client.Invocations.Purge(BeforeUtc);
                }

                if (await ReportHttpStatus(response))
                {
                    await Console.WriteInfoLine("Successfully purged the following invocations:");
                    var purgable = await response.ReadContent();
                    await Console.WriteTable(purgable, i => new
                    {
                        i.Job,
                        i.Status,
                        i.Result,
                        i.Id,
                        CompletedAtLocalTime = i.CompletedAt.Value.ToLocalTime()
                    });
                }
            }
        }
    }
}
