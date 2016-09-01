﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Diagnostics.EventFlow.Configuration;
using Microsoft.Diagnostics.EventFlow.HealthReporters;
using Microsoft.Extensions.Configuration;
using Validation;

namespace Microsoft.Diagnostics.EventFlow
{
    public class DiagnosticsPipelineFactory
    {
        public static DiagnosticsPipeline CreatePipeline(string jsonConfigFilePath)
        {
            IConfiguration config = new ConfigurationBuilder().AddJsonFile(jsonConfigFilePath).Build();

            return CreatePipeline(config);
        }

        public static DiagnosticsPipeline CreatePipeline(IConfiguration configuration)
        {
            Requires.NotNull(configuration, nameof(configuration));

            IHealthReporter healthReporter = CreateHealthReporter(configuration);
            Requires.NotNull(healthReporter, nameof(healthReporter));

            IDictionary<string, string> inputFactories;
            IDictionary<string, string> outputFactories;
            IDictionary<string, string> filterFactories;
            CreateItemFactories(configuration, healthReporter, out inputFactories, out outputFactories, out filterFactories);


            // Step 1: instantiate inputs
            IConfigurationSection inputConfigurationSection = configuration.GetSection("inputs");
            if (inputConfigurationSection.GetChildren().Count() == 0)
            {
                var errMsg = $"{nameof(DiagnosticsPipelineFactory)}: 'inputs' configuration section missing";
                healthReporter.ReportProblem(errMsg);
                throw new Exception(errMsg);
            }

            List<ItemWithChildren<IObservable<EventData>, object>> inputCreationResult;
            if (!ProcessSection<IObservable<EventData>, object>(
                inputConfigurationSection, 
                healthReporter, 
                inputFactories,
                childFactories: null,
                childSectionName: null, 
                isOptional:false, 
                createdItems: out inputCreationResult))
            {
                throw new Exception("Failed to create pipeline from 'input' section");
            }
            List<IObservable<EventData>> inputs = inputCreationResult.Select(item => item.Item).ToList();
            Debug.Assert(inputs.Count > 0);


            // Step 2: instantiate global filters (if any)
            IConfigurationSection globalFilterConfigurationSection = configuration.GetSection("filters");
            List<ItemWithChildren<IFilter, object>> globalFilterCreationResult;
            // It completely fine to have a pipeline with no globals filters section, or an empty one
            ProcessSection<IFilter, object>(
                globalFilterConfigurationSection, 
                healthReporter, 
                filterFactories,
                childFactories: null,
                childSectionName: null, 
                isOptional: true, 
                createdItems: out globalFilterCreationResult);
            List<IFilter> globalFilters = globalFilterCreationResult.Select(item => item.Item).ToList();


            // Step 3: instantiate outputs
            IConfigurationSection outputConfigurationSection = configuration.GetSection("outputs");
            if (outputConfigurationSection.GetChildren().Count() == 0)
            {
                DisposeOf(inputs);

                var errMsg = $"{nameof(DiagnosticsPipelineFactory)}: 'outputs' configuration section missing";
                healthReporter.ReportProblem(errMsg);
                throw new Exception(errMsg);
            }

            List<ItemWithChildren<IOutput, IFilter>> outputCreationResult;
            if (!ProcessSection<IOutput, IFilter>(
                outputConfigurationSection,
                healthReporter,
                outputFactories,
                filterFactories,
                childSectionName: "filters",
                isOptional: false,
                createdItems: out outputCreationResult))
            {
                DisposeOf(inputs);

                throw new Exception("Failed to create pipeline from 'output' section");
            }


            // Step 4: assemble and return the pipeline

            // TODO: the globabl filters should really be executed just once, instead of separately for every output.
            IReadOnlyCollection<EventSink> sinks = outputCreationResult.Select(outputResult =>
                new EventSink(outputResult.Item, globalFilters.Concat(outputResult.Children))
            ).ToList();

            DiagnosticsPipeline pipeline = new DiagnosticsPipeline(healthReporter, inputs, sinks);
            return pipeline;
        }

        private static IHealthReporter CreateHealthReporter(IConfiguration configuration)
        {
            // The GetSection() method never returns null. We will have to call the GetChildren() method to determine if the configuration is empty.
            IConfiguration healthReporterConfiguration = configuration.GetSection("healthReporter");
            string healthReporterType = healthReporterConfiguration["type"];

            if (string.IsNullOrEmpty(healthReporterType)
                || healthReporterType.Equals("CsvHealthReporter", StringComparison.OrdinalIgnoreCase))
            {
                if (healthReporterConfiguration.GetChildren().Count() == 0)
                {
                    healthReporterConfiguration = new ConfigurationBuilder().AddInMemoryCollection().Build();
                }

                return new CsvHealthReporter(healthReporterConfiguration);
            }

            // 3rd party HealthReporter, look up the "extensions" section and create the instance dynamically
            IConfiguration extensionsConfiguration = configuration.GetSection("extensions");
            foreach (var extension in extensionsConfiguration.GetChildren())
            {
                if (string.Equals(extension["category"], "healthReporter", StringComparison.OrdinalIgnoreCase)
                    &&  string.Equals(extension["type"], healthReporterType, StringComparison.OrdinalIgnoreCase))
                {
                    var type = Type.GetType(extension["qualifiedTypeName"]);

                    // Consider: Make IHealthReporter an abstract class, so the inherited classes are ensured to have a constructor with parameter IConfiguration
                    return Activator.CreateInstance(type, healthReporterConfiguration) as IHealthReporter;
                }
            }

            return null;
        }

        private static bool ProcessSection<PipelineItemType, PipelineItemChildType>(
            IConfigurationSection configurationSection,
            IHealthReporter healthReporter,
            IDictionary<string, string> itemFactories,
            IDictionary<string, string> childFactories,
            string childSectionName,
            bool isOptional,
            out List<ItemWithChildren<PipelineItemType, PipelineItemChildType>> createdItems)
        {
            Debug.Assert(isOptional || configurationSection != null);
            Debug.Assert(!string.IsNullOrWhiteSpace(configurationSection.Key));
            Debug.Assert(healthReporter != null);
            Debug.Assert(itemFactories != null);
            Debug.Assert((string.IsNullOrEmpty(childSectionName) && childFactories == null) || (!string.IsNullOrEmpty(childSectionName) && childFactories != null));

            createdItems = new List<ItemWithChildren<PipelineItemType, PipelineItemChildType>>();

            if (isOptional && configurationSection == null)
            {
                return true;
            }

            List<IConfigurationSection> itemConfigurationFragments = configurationSection.GetChildren().ToList();
            if (itemConfigurationFragments.Count == 0)
            {
                if (!isOptional)
                {
                    ReportSectionEmpty(configurationSection, healthReporter);
                }
                return isOptional;
            }

            foreach (var itemFragment in itemConfigurationFragments)
            {
                ItemConfiguration itemConfiguration = new ItemConfiguration();
                try
                {
                    itemFragment.Bind(itemConfiguration);
                }
                catch
                {
                    ReportInvalidConfigurationFragment(healthReporter, itemFragment);
                    continue;
                }

                string itemFactoryTypeName;
                if (!itemFactories.TryGetValue(itemConfiguration.Type, out itemFactoryTypeName))
                {
                    ReportUnknownItemType(configurationSection, healthReporter, itemConfiguration);
                    continue;
                }

                IPipelineItemFactory<PipelineItemType> factory;
                PipelineItemType item;
                try
                {
                    var itemFactoryType = Type.GetType(itemFactoryTypeName);
                    factory = Activator.CreateInstance(itemFactoryType) as IPipelineItemFactory<PipelineItemType>; 
                    if (factory == null)
                    {
                        ReportItemCreationFailed(healthReporter, itemConfiguration.Type);
                    }
                    item = factory.CreateItem(itemFragment, healthReporter);
                }
                catch (Exception e)
                {
                    ReportItemCreationFailed(healthReporter, itemConfiguration.Type, e);
                    continue;
                }
                 
                // The factory will do its own error reporting, so if it returns null, no further error reporting is necessary.
                if (item == null)
                {
                    continue;
                }

                List<ItemWithChildren<PipelineItemChildType, object>> children = null;
                if (!string.IsNullOrEmpty(childSectionName))
                {
                    IConfigurationSection childrenSection = itemFragment.GetSection(childSectionName);
                    ProcessSection<PipelineItemChildType, object>(
                        childrenSection, 
                        healthReporter, 
                        childFactories, 
                        childFactories: null,       // Only one level of nexting is supported
                        childSectionName: null,
                        isOptional: true, 
                        createdItems: out children);

                    createdItems.Add(new ItemWithChildren<PipelineItemType, PipelineItemChildType>(item, children.Select(c => c.Item).ToList()));
                }
                else
                {
                    createdItems.Add(new ItemWithChildren<PipelineItemType, PipelineItemChildType>(item, null));
                }
            }

            if (createdItems.Count == 0 && !isOptional)
            {
                ReportNoItemsCreated(configurationSection, healthReporter);
                return false;
            }

            return true;
        }

        private static void ReportItemCreationFailed(IHealthReporter healthReporter, string itemType, Exception e = null)
        {
            string errorMessage = $"{nameof(DiagnosticsPipelineFactory)}: item of type '{itemType}' could not be created";
            if (e != null)
            {
                errorMessage += Environment.NewLine + e.ToString();
            }
            healthReporter.ReportWarning(errorMessage);
        }

        private static void ReportInvalidConfigurationFragment(IHealthReporter healthReporter, IConfigurationSection itemFragment)
        {
            healthReporter.ReportWarning($"{nameof(DiagnosticsPipelineFactory)}: invalid configuration fragment:{Environment.NewLine}{itemFragment.Value}");
        }

        private static void ReportSectionEmpty(IConfigurationSection configurationSection, IHealthReporter healthReporter)
        {
            healthReporter.ReportWarning($"{nameof(DiagnosticsPipelineFactory)}: '{configurationSection.Key}' configuration section is empty");
        }

        private static void ReportNoItemsCreated(IConfigurationSection configurationSection, IHealthReporter healthReporter)
        {
            healthReporter.ReportWarning($"{nameof(DiagnosticsPipelineFactory)}: could not create any pipeline items out of configuration section '{configurationSection.Key}'");
        }

        private static void ReportUnknownItemType(IConfigurationSection configurationSection, IHealthReporter healthReporter, ItemConfiguration itemConfiguration)
        {
            healthReporter.ReportWarning($"{nameof(DiagnosticsPipelineFactory)}: unknown type '{itemConfiguration.Type}' in configuration section '{configurationSection.Key}'");
        }

        private static void CreateItemFactories(
            IConfiguration configuration, 
            IHealthReporter healthReporter,
            out IDictionary<string, string> inputFactories,
            out IDictionary<string, string> outputFactories,
            out IDictionary<string, string> filterFactories)
        {
            // TODO: finalize the set of "well-known" pipeline elements
            // CONSIDER: make event the well-known pipeline elements overridable
            // TODO: add proper PublicKeyToken to factory references when compiling relase bits


            Debug.Assert(configuration != null);
            Debug.Assert(healthReporter != null);

            inputFactories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            inputFactories["EventSource"] = "Microsoft.Diagnostics.EventFlow.Inputs.EventSourceInputFactory, Microsoft.Diagnostics.EventFlow.Inputs.EventSource, Culture=neutral, PublicKeyToken=null";
            inputFactories["PerformanceCounter"] = "Microsoft.Diagnostics.EventFlow.PerformanceCounterInputFactory, Microsoft.Diagnostics.EventFlow.Inputs.PerformanceCounter, Culture=neutral, PublicKeyToken=null";
            inputFactories["Trace"] = "Microsoft.Diagnostics.EventFlow.Inputs.TraceInputFactory, Microsoft.Diagnostics.EventFlow.Inputs.Trace, Culture=neutral, PublicKeyToken=null";

            outputFactories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            outputFactories["ApplicationInsights"] = "Microsoft.Diagnostics.EventFlow.Outputs.ApplicationInsightsOutputFactory, Microsoft.Diagnostics.EventFlow.Outputs.ApplicationInsights, Culture=neutral, PublicKeyToken=null";
            outputFactories["StdOutput"] = "Microsoft.Diagnostics.EventFlow.Outputs.StdOutputFactory, Microsoft.Diagnostics.EventFlow.Outputs.StdOutput, Culture=neutral, PublicKeyToken=null";

            filterFactories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            filterFactories["metadata"] = "Microsoft.Diagnostics.EventFlow.Filters.EventMetadataFilterFactory, Microsoft.Diagnostics.EventFlow.Core, Culture=neutral, PublicKeyToken=null";
            filterFactories["drop"] = "Microsoft.Diagnostics.EventFlow.Filters.DropFilterFactory, Microsoft.Diagnostics.EventFlow.Core, Culture=neutral, PublicKeyToken=null";

            // TODO: implement 3rd party input/output/filter instantiation driven by the contents of "extensions" section
        }

        private static void DisposeOf(IEnumerable<object> items)
        {
            foreach (var item in items)
            {
                (item as IDisposable)?.Dispose();
            }
        }

        private class ItemWithChildren<ItemType, ChildType>
        {
            public ItemWithChildren(ItemType item, IEnumerable<ChildType> children)
            {
                Debug.Assert(item != null);
                Item = item;
                Children = children;
            }

            public ItemType Item;
            public IEnumerable<ChildType> Children;
        }
    }
}
