﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Threading;
using System.Threading.Tasks;
using NuGet.PackageManagement.VisualStudio;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectManagement;
using NuGet.Resolver;
using NuGet.Versioning;

namespace NuGet.PackageManagement.PowerShellCmdlets
{
    [Cmdlet(VerbsData.Update, "Package", DefaultParameterSetName = "All")]
    public class UpdatePackageCommand : PackageActionBaseCommand
    {
        private ResolutionContext _context;
        private UninstallationContext _uninstallcontext;
        private string _id;
        private string _projectName;
        private bool _idSpecified;
        private bool _projectSpecified;
        private bool _versionSpecifiedPrerelease;
        private bool _allowPrerelease;
        private bool _isPackageInstalled;
        private DependencyBehavior _updateVersionEnum;
        private NuGetVersion _nugetVersion;

        [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true, Position = 0, ParameterSetName = "Project")]
        [Parameter(ValueFromPipelineByPropertyName = true, Position = 0, ParameterSetName = "All")]
        [Parameter(ValueFromPipelineByPropertyName = true, Position = 0, ParameterSetName = "Reinstall")]
        public override string Id
        {
            get { return _id; }
            set
            {
                _id = value;
                _idSpecified = true;
            }
        }

        [Parameter(Position = 1, ValueFromPipelineByPropertyName = true, ParameterSetName = "All")]
        [Parameter(Position = 1, ValueFromPipelineByPropertyName = true, ParameterSetName = "Project")]
        [Parameter(Position = 1, ValueFromPipelineByPropertyName = true, ParameterSetName = "Reinstall")]
        public override string ProjectName
        {
            get { return _projectName; }
            set
            {
                _projectName = value;
                _projectSpecified = true;
            }
        }

        [Parameter(Position = 2, ParameterSetName = "Project")]
        [ValidateNotNullOrEmpty]
        public override string Version { get; set; }

        [Parameter]
        public SwitchParameter Safe { get; set; }

        [Parameter(Mandatory = true, ParameterSetName = "Reinstall")]
        [Parameter(ParameterSetName = "All")]
        public SwitchParameter Reinstall { get; set; }

        private List<NuGetProject> Projects { get; set; }

        public bool IsVersionEnum { get; set; }

        protected override void Preprocess()
        {
            base.Preprocess();
            ParseUserInputForVersion();
            if (!_projectSpecified)
            {
                Projects = VsSolutionManager.GetNuGetProjects().ToList();
            }
            else
            {
                Projects = new List<NuGetProject> { Project };
            }
        }

        protected override void ProcessRecordCore()
        {
            Preprocess();

            SubscribeToProgressEvents();
            PerformPackageUpdatesOrReinstalls();
            UnsubscribeFromProgressEvents();
        }

        /// <summary>
        /// Perform package updates or reinstalls
        /// </summary>
        private void PerformPackageUpdatesOrReinstalls()
        {
            // Update-Package without ID specified
            if (!_idSpecified)
            {
                Task.Run(() => UpdateOrReinstallAllPackages());
            }
            // Update-Package with Id specified
            else
            {
                Task.Run(() => UpdateOrReinstallSinglePackage());
            }
            WaitAndLogPackageActions();
        }

        /// <summary>
        /// Update or reinstall all packages installed to a solution. For Update-Package or Update-Package -Reinstall.
        /// </summary>
        /// <returns></returns>
        private async Task UpdateOrReinstallAllPackages()
        {
            try
            {
                foreach (NuGetProject project in Projects)
                {
                    await PreviewAndExecuteUpdateActionsforAllPackages(project);
                }
            }
            catch (Exception ex)
            {
                Log(MessageLevel.Error, ex.Message);
            }
            finally
            {
                BlockingCollection.Add(new ExecutionCompleteMessage());
            }
        }

        /// <summary>
        /// Update or reinstall a single package installed to a solution. For Update-Package -Id or Update-Package -Id
        /// -Reinstall.
        /// </summary>
        /// <returns></returns>
        private async Task UpdateOrReinstallSinglePackage()
        {
            try
            {
                foreach (NuGetProject project in Projects)
                {
                    await PreviewAndExecuteUpdateActionsforSinglePackage(project);
                }

                if (!_isPackageInstalled)
                {
                    Log(MessageLevel.Error, Resources.PackageNotInstalledInAnyProject, Id);
                }
            }
            catch (Exception ex)
            {
                Log(MessageLevel.Error, ex.Message);
            }
            finally
            {
                BlockingCollection.Add(new ExecutionCompleteMessage());
            }
        }

        /// <summary>
        /// Preview update actions for all packages
        /// </summary>
        /// <param name="project"></param>
        /// <returns></returns>
        private async Task PreviewAndExecuteUpdateActionsforAllPackages(NuGetProject project)
        {
            var token = CancellationToken.None;
            IEnumerable<NuGetProjectAction> actions = Enumerable.Empty<NuGetProjectAction>();

            // Get the list of package ids or identities to be updated for PackageManager
            if (Reinstall.IsPresent)
            {
                // Update-Package -Reinstall -> get list of installed package identities
                IEnumerable<PackageIdentity> identitiesToUpdate = Enumerable.Empty<PackageIdentity>();
                identitiesToUpdate = (await project.GetInstalledPackagesAsync(token)).Select(v => v.PackageIdentity);
                // Preview Update-Package -Reinstall actions
                actions = await PackageManager.PreviewReinstallPackagesAsync(identitiesToUpdate, project, ResolutionContext,
                    this, ActiveSourceRepository, null, token);
            }
            else
            {
                if (Safe.IsPresent)
                {
                    // Update-Package -Safe -> get list of installed package references
                    IEnumerable<PackageReference> installedReferences = Enumerable.Empty<PackageReference>();
                    installedReferences = await project.GetInstalledPackagesAsync(token);
                    foreach (PackageReference reference in installedReferences)
                    {
                        PackageIdentity update = GetPackageUpdate(reference, project, _allowPrerelease, true, null, false, DependencyBehavior.HighestPatch);
                        if (update.Version > reference.PackageIdentity.Version)
                        {
                            IEnumerable<NuGetProjectAction> packageActions = await PackageManager.PreviewInstallPackageAsync(project, update, ResolutionContext, this, ActiveSourceRepository, null, token);
                            if (packageActions != null
                                && packageActions.Any())
                            {
                                actions = actions.Concat(packageActions);
                            }
                        }
                    }
                }
                else
                {
                    // Update-Package -> get list of installed package ids
                    IEnumerable<string> idsToUpdate = Enumerable.Empty<string>();
                    idsToUpdate = await GeneratePackageIdListForUpdate(project, token);
                    // Preview Update-Package actions
                    actions = await PackageManager.PreviewUpdatePackagesAsync(idsToUpdate, project, ResolutionContext,
                        this, ActiveSourceRepository, null, token);
                }
            }

            if (actions.Any())
            {
                if (WhatIf.IsPresent)
                {
                    // For -WhatIf, only preview the actions
                    PreviewNuGetPackageActions(actions);
                }
                else
                {
                    if (Reinstall.IsPresent)
                    {
                        var uninstallActions = actions.Where(a => a.NuGetProjectActionType == NuGetProjectActionType.Uninstall);
                        var installActions = actions.Where(a => a.NuGetProjectActionType == NuGetProjectActionType.Install);

                        // Execute uninstall actions first to ensure that the package is completely uninstalled even from packages folder
                        await PackageManager.ExecuteNuGetProjectActionsAsync(project, uninstallActions, this, CancellationToken.None);

                        // Execute install actions now
                        await PackageManager.ExecuteNuGetProjectActionsAsync(project, installActions, this, CancellationToken.None);
                    }
                    else
                    {
                        // Execute project actions by Package Manager
                        await PackageManager.ExecuteNuGetProjectActionsAsync(project, actions, this, CancellationToken.None);
                    }
                }
            }
            else
            {
                Log(MessageLevel.Info, Resources.Cmdlet_NoPackageUpdates, project.GetMetadata<string>(NuGetProjectMetadataKeys.Name));
            }
        }

        /// <summary>
        /// Preview update actions for single package
        /// </summary>
        /// <param name="project"></param>
        /// <returns></returns>
        private async Task PreviewAndExecuteUpdateActionsforSinglePackage(NuGetProject project)
        {
            var token = CancellationToken.None;
            var installedPackage = (await project.GetInstalledPackagesAsync(token))
                .FirstOrDefault(p => string.Equals(p.PackageIdentity.Id, Id, StringComparison.OrdinalIgnoreCase));

            if (installedPackage != null)
            {
                // set _installed to true, if package to update is installed.
                _isPackageInstalled = true;

                // If -Version switch is specified
                if (!string.IsNullOrEmpty(Version))
                {
                    PackageIdentity update = GetUpdatePackageIdentityWhenVersionSpecified(project, installedPackage);
                    if (update != null)
                    {
                        // Update by package identity
                        await InstallPackageByIdentityAsync(project, update, ResolutionContext, this, WhatIf.IsPresent);
                    }
                    else
                    {
                        Log(MessageLevel.Info, Resources.Cmdlet_NoPackageUpdates, project.GetMetadata<string>(NuGetProjectMetadataKeys.Name));
                    }
                }
                else
                {
                    if (Reinstall.IsPresent)
                    {
                        // Update-Package Id -Reinstall
                        var identitiesToUpdate = new List<PackageIdentity>() { installedPackage.PackageIdentity };
                        var actions = await PackageManager.PreviewReinstallPackagesAsync(identitiesToUpdate, project, ResolutionContext,
                            this, ActiveSourceRepository, null, token);

                        if (WhatIf.IsPresent)
                        {
                            // Preview Update-Package Id -Reinstall actions
                            PreviewNuGetPackageActions(actions);
                        }
                        else
                        {
                            await PackageManager.ExecuteNuGetProjectActionsAsync(project, actions, this, CancellationToken.None);
                        }
                    }
                    else
                    {
                        // Update-Package Id
                        NormalizePackageId(project);
                        if (Safe.IsPresent)
                        {
                            PackageIdentity update = GetPackageUpdate(installedPackage, project, _allowPrerelease, true, null, false, GetDependencyBehavior());
                            await InstallPackageByIdentityAsync(project, update, ResolutionContext, this, WhatIf.IsPresent);
                        }
                        else
                        {
                            await InstallPackageByIdAsync(project, Id, ResolutionContext, this, WhatIf.IsPresent);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Get update package identity when -Version is specified.
        /// </summary>
        /// <param name="project"></param>
        /// <param name="installedPackage"></param>
        /// <returns></returns>
        private PackageIdentity GetUpdatePackageIdentityWhenVersionSpecified(NuGetProject project, PackageReference installedPackage)
        {
            PackageIdentity update = null;
            // If Highest/HighestMinor/HighestPatch/Lowest is given after -version switch
            if (IsVersionEnum)
            {
                update = GetPackageUpdate(installedPackage, project, _allowPrerelease, false, null, true, _updateVersionEnum);
            }
            // If a NuGetVersion format is given after -version switch
            else
            {
                update = GetPackageUpdate(installedPackage, project, _allowPrerelease, false, Version);
            }
            return update;
        }

        /// <summary>
        /// Get a list of package identities with installed package Ids but null versions
        /// </summary>
        /// <param name="project"></param>
        /// <returns></returns>
        private static async Task<IEnumerable<string>> GeneratePackageIdListForUpdate(NuGetProject project, CancellationToken token)
        {
            return (await project.GetInstalledPackagesAsync(token)).Select(v => v.PackageIdentity.Id);
        }

        /// <summary>
        /// Parse user input for -Version switch
        /// </summary>
        private void ParseUserInputForVersion()
        {
            if (!string.IsNullOrEmpty(Version))
            {
                DependencyBehavior updateVersion;
                IsVersionEnum = Enum.TryParse(Version, true, out updateVersion);
                if (IsVersionEnum)
                {
                    _updateVersionEnum = updateVersion;
                }
                // If Version is prerelease, automatically allow prerelease (i.e. append -Prerelease switch).
                else
                {
                    _nugetVersion = PowerShellCmdletsUtility.GetNuGetVersionFromString(Version);
                    if (_nugetVersion.IsPrerelease)
                    {
                        _versionSpecifiedPrerelease = true;
                    }
                }
            }
            _allowPrerelease = IncludePrerelease.IsPresent || _versionSpecifiedPrerelease;
        }

        /// <summary>
        /// Resolution Context for Update-Package command
        /// </summary>
        public ResolutionContext ResolutionContext
        {
            get
            {
                _context = new ResolutionContext(GetDependencyBehavior(), _allowPrerelease, false);
                return _context;
            }
        }

        /// <summary>
        /// Uninstallation Context for Update-Package -Reinstall command
        /// </summary>
        public UninstallationContext UninstallContext
        {
            get
            {
                _uninstallcontext = new UninstallationContext(false, Reinstall.IsPresent);
                return _uninstallcontext;
            }
        }

        /// <summary>
        /// Return dependecy behavior for Update-Package command.
        /// </summary>
        /// <returns></returns>
        protected override DependencyBehavior GetDependencyBehavior()
        {
            // Return DependencyBehavior.Highest for Update-Package
            if (!_idSpecified
                && !Reinstall.IsPresent)
            {
                return DependencyBehavior.Highest;
            }

            return base.GetDependencyBehavior();
        }
    }
}