// Copyright (c) Microsoft. All rights reserved.
namespace Microsoft.Azure.Devices.Edge.Agent.Kubernetes
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using k8s;
    using k8s.Models;
    using Microsoft.Azure.Devices.Edge.Agent.Core;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;
    using AgentDocker = Microsoft.Azure.Devices.Edge.Agent.Docker;
    using CoreConstants = Microsoft.Azure.Devices.Edge.Agent.Core.Constants;

    // TODO add unit tests
    public class EdgeDeploymentController : IEdgeDeploymentController
    {
        const string EdgeHubHostname = "edgehub";

        readonly IKubernetes client;

        readonly ResourceName resourceName;
        readonly string edgeHostname;
        readonly string deploymentSelector;
        readonly string proxyImage;
        readonly string proxyConfigPath;
        readonly string proxyConfigVolumeName;
        readonly string proxyConfigMapName;
        readonly string proxyTrustBundlePath;
        readonly string proxyTrustBundleVolumeName;
        readonly string proxyTrustBundleConfigMapName;
        readonly PortMapServiceType defaultMapServiceType;
        readonly string workloadApiVersion;
        readonly string deviceNamespace;
        readonly Uri workloadUri;
        readonly Uri managementUri;
        readonly IModuleIdentityLifecycleManager moduleIdentityLifecycleManager;

        public EdgeDeploymentController(
            ResourceName resourceName,
            string edgeHostname,
            string proxyImage,
            string proxyConfigPath,
            string proxyConfigVolumeName,
            string proxyConfigMapName,
            string proxyTrustBundlePath,
            string proxyTrustBundleVolumeName,
            string proxyTrustBundleConfigMapName,
            string deploymentSelector,
            PortMapServiceType defaultMapServiceType,
            string deviceNamespace,
            string workloadApiVersion,
            Uri workloadUri,
            Uri managementUri,
            IKubernetes client,
            IModuleIdentityLifecycleManager moduleIdentityLifecycleManager)
        {
            this.resourceName = resourceName;
            this.edgeHostname = edgeHostname;
            this.proxyImage = proxyImage;
            this.proxyConfigPath = proxyConfigPath;
            this.proxyConfigVolumeName = proxyConfigVolumeName;
            this.proxyConfigMapName = proxyConfigMapName;
            this.proxyTrustBundlePath = proxyTrustBundlePath;
            this.proxyTrustBundleVolumeName = proxyTrustBundleVolumeName;
            this.proxyTrustBundleConfigMapName = proxyTrustBundleConfigMapName;
            this.deploymentSelector = deploymentSelector;
            this.defaultMapServiceType = defaultMapServiceType;
            this.deviceNamespace = deviceNamespace;
            this.workloadApiVersion = workloadApiVersion;
            this.workloadUri = workloadUri;
            this.managementUri = managementUri;
            this.moduleIdentityLifecycleManager = moduleIdentityLifecycleManager;
            this.client = client;
        }

        string DeploymentName(string moduleId) => KubeUtils.SanitizeK8sValue(moduleId);

        public async Task<ModuleSet> DeployModulesAsync(IList<KubernetesModule> modules, ModuleSet currentModules)
        {
            var desiredModules = ModuleSet.Create(modules.ToArray());
            var moduleIdentities = await this.moduleIdentityLifecycleManager.GetModuleIdentitiesAsync(desiredModules, currentModules);

            var desiredServices = new List<V1Service>();
            var desiredDeployments = new List<V1Deployment>();

            // Bootstrap the module builder
            var kubernetesModelBuilder = new KubernetesModelBuilder(this.proxyImage, this.proxyConfigPath, this.proxyConfigVolumeName, this.proxyConfigMapName, this.proxyTrustBundlePath, this.proxyTrustBundleVolumeName, this.proxyTrustBundleConfigMapName, this.defaultMapServiceType);

            foreach (KubernetesModule module in modules)
            {
                if (!string.Equals(module.Type, "docker"))
                {
                    Events.InvalidModuleType(module);
                    continue;
                }

                var moduleId = moduleIdentities[module.Name];
                string deploymentName = this.DeploymentName(moduleIdentities[module.Name].ModuleId);

                // Default labels
                var labels = new Dictionary<string, string>
                {
                    [Constants.K8sEdgeModuleLabel] = deploymentName,
                    [Constants.K8sEdgeDeviceLabel] = KubeUtils.SanitizeLabelValue(this.resourceName.DeviceId),
                    [Constants.K8sEdgeHubNameLabel] = KubeUtils.SanitizeLabelValue(this.resourceName.Hostname)
                };

                // Create a Pod for each module, and a proxy container.
                List<V1EnvVar> envVars = this.CollectEnv(module, moduleId);

                // Load the current module
                kubernetesModelBuilder.LoadModule(labels, module, moduleId, envVars);

                // Create a Service for every network interface of each module. (label them with hub, device and module id)
                Option<V1Service> moduleService = kubernetesModelBuilder.GetService();
                moduleService.ForEach(service => desiredServices.Add(service));

                // Get the converted pod
                V1PodTemplateSpec v1PodSpec = kubernetesModelBuilder.GetPod();

                // Deployment data
                var deploymentMeta = new V1ObjectMeta(name: deploymentName, labels: labels);

                var selector = new V1LabelSelector(matchLabels: labels);
                var deploymentSpec = new V1DeploymentSpec(replicas: 1, selector: selector, template: v1PodSpec);

                desiredDeployments.Add(new V1Deployment(metadata: deploymentMeta, spec: deploymentSpec));
            }

            V1ServiceList currentServices = await this.client.ListNamespacedServiceAsync(this.deviceNamespace, labelSelector: this.deploymentSelector);
            await this.ManageServices(currentServices, desiredServices);

            V1DeploymentList currentDeployments = await this.client.ListNamespacedDeploymentAsync(this.deviceNamespace, labelSelector: this.deploymentSelector);
            await this.ManageDeployments(currentDeployments, desiredDeployments);

            return desiredModules;
        }

        async Task ManageServices(V1ServiceList currentServices, List<V1Service> desiredServices)
        {
            Dictionary<string, string> currentV1ServicesFromAnnotations = this.GetCurrentServiceConfig(currentServices);

            // Figure out what to remove
            var servicesRemoved = new List<V1Service>(currentServices.Items);
            servicesRemoved.RemoveAll(s => desiredServices.Exists(i => string.Equals(i.Metadata.Name, s.Metadata.Name)));

            // Figure out what to create
            var newServices = new List<V1Service>();
            desiredServices.ForEach(
                service =>
                {
                    string creationString = JsonConvert.SerializeObject(service);

                    if (currentV1ServicesFromAnnotations.ContainsKey(service.Metadata.Name))
                    {
                        string serviceAnnotation = currentV1ServicesFromAnnotations[service.Metadata.Name];
                        // If configuration matches, no need to update service
                        if (string.Equals(serviceAnnotation, creationString))
                        {
                            return;
                        }

                        if (service.Metadata.Annotations == null)
                        {
                            service.Metadata.Annotations = new Dictionary<string, string>();
                        }

                        service.Metadata.Annotations[Constants.CreationString] = creationString;

                        servicesRemoved.Add(service);
                        newServices.Add(service);
                        Events.UpdateService(service.Metadata.Name);
                    }
                    else
                    {
                        if (service.Metadata.Annotations == null)
                        {
                            service.Metadata.Annotations = new Dictionary<string, string>();
                        }

                        service.Metadata.Annotations[Constants.CreationString] = creationString;

                        newServices.Add(service);
                        Events.CreateService(service.Metadata.Name);
                    }
                });

            // remove the old
            await Task.WhenAll(
                servicesRemoved.Select(
                    i =>
                    {
                        Events.DeletingService(i);
                        return this.client.DeleteNamespacedServiceAsync(i.Metadata.Name, this.deviceNamespace, new V1DeleteOptions());
                    }));

            // Create the new.
            await Task.WhenAll(
                newServices.Select(
                    s =>
                    {
                        Events.CreatingService(s);
                        return this.client.CreateNamespacedServiceAsync(s, this.deviceNamespace);
                    }));
        }

        async Task ManageDeployments(V1DeploymentList currentDeployments, List<V1Deployment> desiredDeployments)
        {
            Dictionary<string, string> currentDeploymentsFromAnnotations = this.GetCurrentDeploymentConfig(currentDeployments);

            var deploymentsRemoved = new List<V1Deployment>(currentDeployments.Items);
            deploymentsRemoved.RemoveAll(
                removedDeployment => { return desiredDeployments.Exists(deployment => string.Equals(deployment.Metadata.Name, removedDeployment.Metadata.Name)); });
            var deploymentsUpdated = new List<V1Deployment>();
            var newDeployments = new List<V1Deployment>();
            List<V1Deployment> currentDeploymentsList = currentDeployments.Items.ToList();
            desiredDeployments.ForEach(
                deployment =>
                {
                    if (currentDeploymentsFromAnnotations.ContainsKey(deployment.Metadata.Name))
                    {
                        V1Deployment current = currentDeploymentsList.Find(i => string.Equals(i.Metadata.Name, deployment.Metadata.Name));
                        string currentFromAnnotation = currentDeploymentsFromAnnotations[deployment.Metadata.Name];
                        string creationString = JsonConvert.SerializeObject(deployment);

                        // If configuration matches, or this is edgeAgent deployment and the images match,
                        // no need to do update deployment
                        if (string.Equals(currentFromAnnotation, creationString) ||
                            (string.Equals(deployment.Metadata.Name, this.DeploymentName(CoreConstants.EdgeAgentModuleName)) && V1DeploymentEx.ImageEquals(current, deployment)))
                        {
                            return;
                        }

                        deployment.Metadata.ResourceVersion = current.Metadata.ResourceVersion;
                        if (deployment.Metadata.Annotations == null)
                        {
                            var annotations = new Dictionary<string, string>
                            {
                                [Constants.CreationString] = creationString
                            };
                            deployment.Metadata.Annotations = annotations;
                        }
                        else
                        {
                            deployment.Metadata.Annotations[Constants.CreationString] = creationString;
                        }

                        deploymentsUpdated.Add(deployment);
                        Events.UpdateDeployment(deployment.Metadata.Name);
                    }
                    else
                    {
                        string creationString = JsonConvert.SerializeObject(deployment);
                        var annotations = new Dictionary<string, string>
                        {
                            [Constants.CreationString] = creationString
                        };
                        deployment.Metadata.Annotations = annotations;
                        newDeployments.Add(deployment);
                        Events.CreateDeployment(deployment.Metadata.Name);
                    }
                });

            // Delete the service accounts that have their deployment being deleted.
            var deletedDeploymentNames = deploymentsRemoved.Select(deployment => deployment.Metadata.Name);

            // Remove the old
            var removeDeploymentsTasks = deploymentsRemoved.Select(
                deployment =>
                {
                    Events.DeletingDeployment(deployment);
                    return this.client.DeleteNamespacedDeployment1Async(deployment.Metadata.Name, this.deviceNamespace, new V1DeleteOptions(propagationPolicy: "Foreground"), propagationPolicy: "Foreground");
                });

            // Create the new deployments
            var createDeploymentsTasks = newDeployments.Select(
                deployment =>
                {
                    Events.CreatingDeployment(deployment);
                    return this.client.CreateNamespacedDeploymentAsync(deployment, this.deviceNamespace);
                });

            // Create the new Service Accounts
            var createServiceAccounts = newDeployments.Select(
                deployment =>
                {
                    Events.CreatingServiceAccount(deployment.Metadata.Name);
                    return this.CreateServiceAccount(deployment);
                });

            // Update the existing - should only do this when different.
            var updateDeploymentsTasks = deploymentsUpdated.Select(deployment => this.client.ReplaceNamespacedDeploymentAsync(deployment, deployment.Metadata.Name, this.deviceNamespace));

            await Task.WhenAll(removeDeploymentsTasks);
            await this.PruneServiceAccounts(deletedDeploymentNames.ToList());
            await Task.WhenAll(createServiceAccounts);
            await Task.WhenAll(createDeploymentsTasks);
            await Task.WhenAll(updateDeploymentsTasks);
        }

        Task<V1ServiceAccount> CreateServiceAccount(V1Deployment deployment)
        {
            V1ServiceAccount account = new V1ServiceAccount();
            var metadata = new V1ObjectMeta();

            string moduleId = deployment.Metadata.Labels[Constants.K8sEdgeModuleLabel];
            metadata.Labels = deployment.Metadata.Labels;
            metadata.Annotations = new Dictionary<string, string>
            {
                [Constants.K8sEdgeOriginalModuleId] = deployment.Spec.Template.Metadata.Annotations[Constants.K8sEdgeOriginalModuleId]
            };

            metadata.Name = moduleId;

            account.Metadata = metadata;

            return this.client.CreateNamespacedServiceAccountAsync(account, this.deviceNamespace);
        }

        async Task PruneServiceAccounts(List<string> accountNamesToPrune)
        {
            var currentServiceAccounts = (await this.client.ListNamespacedServiceAccountAsync(this.deviceNamespace)).Items;

            // Prune down the list to those found in the passed in prune list.
            var accountsToDelete = currentServiceAccounts.Where(serviceAccount => accountNamesToPrune.Contains(serviceAccount.Metadata.Name));

            var deletionTasks = accountsToDelete.Select(
                account =>
                {
                    Events.DeletingServiceAccount(account.Metadata.Name);
                    return this.client.DeleteNamespacedServiceAccountAsync(account.Metadata.Name, this.deviceNamespace);
                });

            await Task.WhenAll(deletionTasks);
        }

        List<V1EnvVar> CollectEnv(IModule<AgentDocker.CombinedDockerConfig> moduleWithDockerConfig, IModuleIdentity identity)
        {
            char[] envSplit = { '=' };
            var envList = new List<V1EnvVar>();
            foreach (KeyValuePair<string, EnvVal> item in moduleWithDockerConfig.Env)
            {
                envList.Add(new V1EnvVar(item.Key, item.Value.Value));
            }

            if (moduleWithDockerConfig.Config?.CreateOptions?.Env != null)
            {
                foreach (string hostEnv in moduleWithDockerConfig.Config?.CreateOptions?.Env)
                {
                    string[] keyValue = hostEnv.Split(envSplit, 2);
                    if (keyValue.Count() == 2)
                    {
                        envList.Add(new V1EnvVar(keyValue[0], keyValue[1]));
                    }
                }
            }

            envList.Add(new V1EnvVar(CoreConstants.IotHubHostnameVariableName, this.resourceName.Hostname));
            envList.Add(new V1EnvVar(CoreConstants.EdgeletAuthSchemeVariableName, "sasToken"));
            envList.Add(new V1EnvVar(Logger.RuntimeLogLevelEnvKey, Logger.GetLogLevel().ToString()));
            envList.Add(new V1EnvVar(CoreConstants.EdgeletWorkloadUriVariableName, this.workloadUri.ToString()));
            if (identity.Credentials is IdentityProviderServiceCredentials creds)
            {
                envList.Add(new V1EnvVar(CoreConstants.EdgeletModuleGenerationIdVariableName, creds.ModuleGenerationId));
            }

            envList.Add(new V1EnvVar(CoreConstants.DeviceIdVariableName, this.resourceName.DeviceId)); // could also get this from module identity
            envList.Add(new V1EnvVar(CoreConstants.ModuleIdVariableName, identity.ModuleId));
            envList.Add(new V1EnvVar(CoreConstants.EdgeletApiVersionVariableName, this.workloadApiVersion));

            if (string.Equals(identity.ModuleId, CoreConstants.EdgeAgentModuleIdentityName))
            {
                envList.Add(new V1EnvVar(CoreConstants.ModeKey, CoreConstants.KubernetesMode));
                envList.Add(new V1EnvVar(CoreConstants.EdgeletManagementUriVariableName, this.managementUri.ToString()));
                envList.Add(new V1EnvVar(CoreConstants.NetworkIdKey, "azure-iot-edge"));
                envList.Add(new V1EnvVar(CoreConstants.ProxyImageEnvKey, this.proxyImage));
                envList.Add(new V1EnvVar(CoreConstants.ProxyConfigPathEnvKey, this.proxyConfigPath));
                envList.Add(new V1EnvVar(CoreConstants.ProxyConfigVolumeEnvKey, this.proxyConfigVolumeName));
                envList.Add(new V1EnvVar(CoreConstants.ProxyConfigMapNameEnvKey, this.proxyConfigMapName));
                envList.Add(new V1EnvVar(CoreConstants.ProxyTrustBundlePathEnvKey, this.proxyTrustBundlePath));
                envList.Add(new V1EnvVar(CoreConstants.ProxyTrustBundleVolumeEnvKey, this.proxyTrustBundleVolumeName));
                envList.Add(new V1EnvVar(CoreConstants.ProxyTrustBundleConfigMapEnvKey, this.proxyTrustBundleConfigMapName));
            }

            if (string.Equals(identity.ModuleId, CoreConstants.EdgeAgentModuleIdentityName) ||
                string.Equals(identity.ModuleId, CoreConstants.EdgeHubModuleIdentityName))
            {
                envList.Add(new V1EnvVar(CoreConstants.EdgeDeviceHostNameKey, this.edgeHostname));
            }
            else
            {
                envList.Add(new V1EnvVar(CoreConstants.GatewayHostnameVariableName, EdgeHubHostname));
            }

            return envList;
        }

        Dictionary<string, string> GetCurrentServiceConfig(V1ServiceList currentServices)
        {
            return currentServices.Items.ToDictionary(
                service =>
                {
                    if (service?.Metadata?.Name != null)
                    {
                        return service.Metadata.Name;
                    }

                    Events.InvalidCreationString("service", "null service");
                    throw new NullReferenceException("null service in list");
                },
                service =>
                {
                    if (service == null)
                    {
                        Events.InvalidCreationString("service", "null service");
                        throw new NullReferenceException("null service in list");
                    }

                    if (service.Metadata?.Annotations != null
                        && service.Metadata.Annotations.TryGetValue(Constants.CreationString, out string creationString))
                    {
                        return creationString;
                    }

                    Events.InvalidCreationString(service.Kind, service.Metadata?.Name);

                    var serviceWithoutStatus = new V1Service(service.ApiVersion, service.Kind, service.Metadata, service.Spec);
                    return JsonConvert.SerializeObject(serviceWithoutStatus);
                });
        }

        Dictionary<string, string> GetCurrentDeploymentConfig(V1DeploymentList currentDeployments)
        {
            return currentDeployments.Items.ToDictionary(
                deployment =>
                {
                    if (deployment?.Metadata?.Name != null)
                    {
                        return deployment.Metadata.Name;
                    }

                    Events.InvalidCreationString("deployment", "null deployment");
                    throw new NullReferenceException("null deployment in list");
                },
                deployment =>
                {
                    if (deployment == null)
                    {
                        Events.InvalidCreationString("deployment", "null deployment");
                        throw new NullReferenceException("null deployment in list");
                    }

                    if (deployment.Metadata?.Annotations != null
                        && deployment.Metadata.Annotations.TryGetValue(Constants.CreationString, out string creationString))
                    {
                        return creationString;
                    }

                    Events.InvalidCreationString(deployment.Kind, deployment.Metadata?.Name);
                    var deploymentWithoutStatus = new V1Deployment(deployment.ApiVersion, deployment.Kind, deployment.Metadata, deployment.Spec);
                    return JsonConvert.SerializeObject(deploymentWithoutStatus);
                });
        }

        public async Task PurgeModulesAsync()
        {
            // Delete all services for current edge deployment
            V1ServiceList currentServices = await this.client.ListNamespacedServiceAsync(this.deviceNamespace, labelSelector: this.deploymentSelector);
            IEnumerable<Task<V1Status>> removeServiceTasks = currentServices.Items.Select(
                service => this.client.DeleteNamespacedServiceAsync(service.Metadata.Name, this.deviceNamespace, new V1DeleteOptions()));
            await Task.WhenAll(removeServiceTasks);

            // Delete all deployments for current edge deployment
            V1DeploymentList currentDeployments = await this.client.ListNamespacedDeploymentAsync(this.deviceNamespace, labelSelector: this.deploymentSelector);
            IEnumerable<Task<V1Status>> removeDeploymentTasks = currentDeployments.Items.Select(
                deployment => this.client.DeleteNamespacedDeployment1Async(
                    deployment.Metadata.Name,
                    this.deviceNamespace,
                    new V1DeleteOptions(propagationPolicy: "Foreground"),
                    propagationPolicy: "Foreground"));
            await Task.WhenAll(removeDeploymentTasks);

            // Remove the service account for all deployments
            var serviceAccountNames = currentDeployments.Items.Select(deployment => deployment.Metadata.Name);
            await this.PruneServiceAccounts(serviceAccountNames.ToList());
        }

        static class Events
        {
            const int IdStart = KubernetesEventIds.EdgeDeploymentController;
            static readonly ILogger Log = Logger.Factory.CreateLogger<EdgeDeploymentController>();

            enum EventIds
            {
                InvalidModuleType = IdStart,
                InvalidCreationString,
                UpdateService,
                CreateService,
                UpdateDeployment,
                CreateDeployment,
                DeletingService,
                DeletingDeployment,
                CreatingDeployment,
                CreatingService,
                CreatingServiceAccount,
                DeletingServiceAccount
            }

            public static void DeletingService(V1Service service)
            {
                Log.LogInformation((int)EventIds.DeletingService, $"Deleting service {service.Metadata.Name}");
            }

            public static void CreatingService(V1Service service)
            {
                Log.LogInformation((int)EventIds.CreatingService, $"Creating service {service.Metadata.Name}");
            }

            public static void DeletingDeployment(V1Deployment deployment)
            {
                Log.LogInformation((int)EventIds.DeletingDeployment, $"Deleting deployment {deployment.Metadata.Name}");
            }

            public static void CreatingDeployment(V1Deployment deployment)
            {
                Log.LogInformation((int)EventIds.CreatingDeployment, $"Creating deployment {deployment.Metadata.Name}");
            }

            public static void InvalidModuleType(IModule module)
            {
                Log.LogError((int)EventIds.InvalidModuleType, $"Module {module.Name} has an invalid module type '{module.Type}'. Expected type 'docker'");
            }

            public static void InvalidCreationString(string kind, string name)
            {
                Log.LogDebug((int)EventIds.InvalidCreationString, $"Expected a valid '{kind}' creation string in k8s Object '{name}'.");
            }

            public static void UpdateService(string name)
            {
                Log.LogDebug((int)EventIds.UpdateService, $"Updating service object '{name}'");
            }

            public static void CreateService(string name)
            {
                Log.LogDebug((int)EventIds.CreateService, $"Creating service object '{name}'");
            }

            public static void UpdateDeployment(string name)
            {
                Log.LogDebug((int)EventIds.UpdateDeployment, $"Updating edge deployment '{name}'");
            }

            public static void CreateDeployment(string name)
            {
                Log.LogDebug((int)EventIds.CreateDeployment, $"Creating edge deployment '{name}'");
            }

            public static void CreatingServiceAccount(string name)
            {
                Log.LogDebug((int)EventIds.CreatingServiceAccount, $"Creating Service Account {name}");
            }

            public static void DeletingServiceAccount(string name)
            {
                Log.LogDebug((int)EventIds.DeletingServiceAccount, $"Deleting Service Account {name}");
            }
       }
    }
}
