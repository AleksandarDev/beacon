using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Signal.Beacon.Application.Conducts;
using Signal.Beacon.Core.Conditions;
using Signal.Beacon.Core.Conducts;
using Signal.Beacon.Core.Devices;
using Signal.Beacon.Core.Processes;

namespace Signal.Beacon.Application.Processing
{
    internal class Processor : IProcessor
    {
        private readonly IConditionEvaluatorService conditionEvaluatorService;
        private readonly IProcessesService processesService;
        private readonly IDeviceStateManager deviceStateManager;
        private readonly IConductManager conductManager;
        private readonly ILogger<Processor> logger;

        public Processor(
            IConditionEvaluatorService conditionEvaluatorService,
            IProcessesService processesService,
            IDeviceStateManager deviceStateManager,
            IConductManager conductManager,
            ILogger<Processor> logger)
        {
            this.conditionEvaluatorService = conditionEvaluatorService ?? throw new ArgumentNullException(nameof(conditionEvaluatorService));
            this.processesService = processesService ?? throw new ArgumentNullException(nameof(processesService));
            this.deviceStateManager = deviceStateManager ?? throw new ArgumentNullException(nameof(deviceStateManager));
            this.conductManager = conductManager ?? throw new ArgumentNullException(nameof(conductManager));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }


        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Subscribe to state changes
            this.deviceStateManager.Subscribe(this.ProcessStateChangedAsync);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
        
        private async Task ProcessStateChangedAsync(DeviceTarget target, CancellationToken cancellationToken)
        {
            var processes = await this.processesService.GetStateTriggeredAsync(cancellationToken);
            var applicableProcesses = processes
                .Where(p => !p.IsDisabled && (p.Triggers?.Any(t => t == target) ?? false))
                .ToList();
            if (!applicableProcesses.Any())
            {
                this.logger.LogTrace("Change on target {DeviceEndpointTarget} ignored.", target);
                return;
            }

            // Collect all process conducts that meet conditions
            var conducts = new List<Conduct>();
            foreach (var process in applicableProcesses)
            {
                try
                {
                    // Ignore if condition not met
                    if (!await this.conditionEvaluatorService.IsConditionMetAsync(process.Condition, cancellationToken))
                        continue;

                    // Queue conducts
                    this.logger.LogInformation(
                        "Process \"{ProcessName}\" queued... (trigger {Target})",
                        process.Alias, target);
                    conducts.AddRange(process.Conducts);
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex, "StateTriggerProcess condition invalid. Recheck your configuration. ProcessName: {ProcessName}", process.Alias);
                }
            }

            // Execute all conducts
            await this.conductManager.PublishAsync(conducts, cancellationToken);
        }
    }
}