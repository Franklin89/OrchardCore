﻿using OrchardVNext.Hosting.Descriptor.Models;
using OrchardVNext.Hosting.Extensions;
using OrchardVNext.Hosting.Extensions.Models;
using OrchardVNext.Hosting.ShellBuilders.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Dnx.Runtime;
using OrchardVNext.Configuration.Environment;
using OrchardVNext.DependencyInjection;
using Microsoft.Framework.Logging;

namespace OrchardVNext.Hosting.ShellBuilders
{
    /// <summary>
    /// Service at the host level to transform the cachable descriptor into the loadable blueprint.
    /// </summary>
    public interface ICompositionStrategy
    {
        /// <summary>
        /// Using information from the IExtensionManager, transforms and populates all of the
        /// blueprint model the shell builders will need to correctly initialize a tenant IoC container.
        /// </summary>
        ShellBlueprint Compose(ShellSettings settings, ShellDescriptor descriptor);
    }

    public class CompositionStrategy : ICompositionStrategy {
        private readonly IExtensionManager _extensionManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        public CompositionStrategy(IExtensionManager extensionManager,
            ILibraryManager libraryManager,
            ILoggerFactory loggerFactory) {
            _extensionManager = extensionManager;
            _libraryManager = libraryManager;
            _logger = loggerFactory.CreateLogger<CompositionStrategy>();
        }

        public ShellBlueprint Compose(ShellSettings settings, ShellDescriptor descriptor) {
            _logger.LogDebug("Composing blueprint");

            var enabledFeatures = _extensionManager.EnabledFeatures(descriptor);
            var features = _extensionManager.LoadFeatures(enabledFeatures);

            if (descriptor.Features.Any(feature => feature.Name == "OrchardVNext.Hosting"))
                features = BuiltinFeatures().Concat(features);

            var excludedTypes = GetExcludedTypes(features);

            var modules = BuildBlueprint(features, IsModule, BuildModule, excludedTypes);
            var dependencies = BuildBlueprint(features, IsDependency, (t, f) => BuildDependency(t, f, descriptor),
                excludedTypes);

            var result = new ShellBlueprint {
                Settings = settings,
                Descriptor = descriptor,
                Dependencies = dependencies.Concat(modules).ToArray()
            };

            _logger.LogDebug("Done composing blueprint");
            return result;
        }

        private static IEnumerable<string> GetExcludedTypes(IEnumerable<Feature> features) {
            var excludedTypes = new HashSet<string>();

            // Identify replaced types
            foreach (Feature feature in features) {
                foreach (Type type in feature.ExportedTypes) {
                    foreach (
                        OrchardSuppressDependencyAttribute replacedType in
                            type.GetTypeInfo().GetCustomAttributes(typeof (OrchardSuppressDependencyAttribute), false)) {
                        excludedTypes.Add(replacedType.FullName);
                    }
                }
            }

            return excludedTypes;
        }

        private IEnumerable<Feature> BuiltinFeatures() {
            var additionalLibraries = _libraryManager
                .GetLibraries()
                .Where(x => x.Name.StartsWith("OrchardVNext"))
                .Select(x => Assembly.Load(new AssemblyName(x.Name)));

            foreach (var additonalLib in additionalLibraries) {
                yield return new Feature {
                    Descriptor = new FeatureDescriptor {
                        Id = additonalLib.GetName().Name,
                        Extension = new ExtensionDescriptor {
                            Id = additonalLib.GetName().Name
                        }
                    },
                    ExportedTypes =
                        additonalLib.ExportedTypes
                            .Where(t => t.GetTypeInfo().IsClass && !t.GetTypeInfo().IsAbstract)
                            .Except(new[] { typeof(DefaultOrchardHost) })
                            .ToArray()
                };
            }

        }

        private static IEnumerable<T> BuildBlueprint<T>(
            IEnumerable<Feature> features,
            Func<Type, bool> predicate,
            Func<Type, Feature, T> selector,
            IEnumerable<string> excludedTypes) {

            // Load types excluding the replaced types
            return features.SelectMany(
                feature => feature.ExportedTypes
                    .Where(predicate)
                    .Where(type => !excludedTypes.Contains(type.FullName))
                    .Select(type => selector(type, feature)))
                .ToArray();
        }
        
        private static bool IsModule(Type type) {
            return typeof (IModule).IsAssignableFrom(type);
        }

        private static DependencyBlueprint BuildModule(Type type, Feature feature) {
            return new DependencyBlueprint {
                Type = type,
                Feature = feature,
                Parameters = Enumerable.Empty<ShellParameter>()
            };
        }
        
        private static bool IsDependency(Type type) {
            return typeof (IDependency).IsAssignableFrom(type);
        }

        private static DependencyBlueprint BuildDependency(Type type, Feature feature, ShellDescriptor descriptor) {
            return new DependencyBlueprint {
                Type = type,
                Feature = feature,
                Parameters = descriptor.Parameters.Where(x => x.Component == type.FullName).ToArray()
            };
        }
    }
}