﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PowerShell.EditorServices.Services;
using Microsoft.PowerShell.EditorServices.Services.DebugAdapter;
using Microsoft.PowerShell.EditorServices.Utility;
using OmniSharp.Extensions.DebugAdapter.Protocol.Models;
using OmniSharp.Extensions.DebugAdapter.Protocol.Requests;

namespace Microsoft.PowerShell.EditorServices.Handlers
{
    internal class StackTraceHandler : IStackTraceHandler
    {
        private readonly DebugService _debugService;

        public StackTraceHandler(DebugService debugService) => _debugService = debugService;

        public Task<StackTraceResponse> Handle(StackTraceArguments request, CancellationToken cancellationToken)
        {
            StackFrameDetails[] stackFrameDetails =
                _debugService.GetStackFrames(cancellationToken);

            // Handle a rare race condition where the adapter requests stack frames before they've
            // begun building.
            if (stackFrameDetails == null)
            {
                return Task.FromResult(new StackTraceResponse
                {
                    StackFrames = Array.Empty<StackFrame>(),
                    TotalFrames = 0
                });
            }

            List<StackFrame> newStackFrames = new();

            long startFrameIndex = request.StartFrame ?? 0;
            long maxFrameCount = stackFrameDetails.Length;

            // If the number of requested levels == 0 (or null), that means get all stack frames
            // after the specified startFrame index. Otherwise get all the stack frames.
            long requestedFrameCount = request.Levels ?? 0;
            if (requestedFrameCount > 0)
            {
                maxFrameCount = Math.Min(maxFrameCount, startFrameIndex + requestedFrameCount);
            }

            for (long i = startFrameIndex; i < maxFrameCount; i++)
            {
                // Create the new StackFrame object with an ID that can
                // be referenced back to the current list of stack frames
                //newStackFrames.Add(
                //    StackFrame.Create(
                //        stackFrameDetails[i],
                //        i));
                newStackFrames.Add(
                    LspDebugUtils.CreateStackFrame(stackFrameDetails[i], id: i));
            }

            return Task.FromResult(new StackTraceResponse
            {
                StackFrames = newStackFrames,
                TotalFrames = newStackFrames.Count
            });
        }
    }
}
