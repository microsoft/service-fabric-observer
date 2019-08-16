// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

// This file is used by Code Analysis to maintain SuppressMessage 
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given 
// a specific target and scoped to a namespace, type, member, etc.

[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "<Pending>", Scope = "type", Target = "~T:FabricObserver.Model.ApplicationInfo")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "<Pending>", Scope = "member", Target = "~M:FabricObserver.ObserverBase.EmitLogEvent(System.String,System.String,FabricObserver.Utilities.LogEventLevel)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "<Pending>", Scope = "type", Target = "~T:FabricObserver.OSObserver")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "<Pending>", Scope = "type", Target = "~T:FabricObserver.FabricObserver")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "<Pending>", Scope = "type", Target = "~T:FabricObserver.AppObserver")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "<Pending>", Scope = "type", Target = "~T:FabricObserver.FabricSystemObserver")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "<Pending>", Scope = "type", Target = "~T:FabricObserver.NodeObserver")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "<Pending>", Scope = "member", Target = "~M:FabricObserver.FabricSystemObserver.GetActivePortCountString~System.String")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "We don't want to crash ObserverManager just because an Observer threw...", Scope = "member", Target = "~M:FabricObserver.ObserverManager.RunObservers~System.Boolean")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = " We are adding all exceptions to a list, so it makes no sense to throw here...", Scope = "member", Target = "~M:FabricObserver.Utilities.Retry.Do``1(System.Func{``0},System.TimeSpan,System.Threading.CancellationToken,System.Int32)~``0")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "<Pending>", Scope = "member", Target = "~M:FabricObserver.Utilities.ObserverHealthReporter.ReportServiceHealth(System.String,System.String,System.Fabric.Health.HealthState,System.String)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "<Pending>", Scope = "type", Target = "~T:FabricObserver.NetworkObserver")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "<Pending>", Scope = "member", Target = "~M:FabricObserver.ObserverManager.Start")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1063:Implement IDisposable Correctly", Justification = "<Pending>", Scope = "member", Target = "~M:FabricObserver.ObserverBase.Dispose")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "<Pending>", Scope = "member", Target = "~P:FabricObserver.Model.NetworkObserverConfig.Endpoints")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3075:Insecure DTD processing in XML", Justification = "This is a false positive...", Scope = "member", Target = "~M:FabricObserver.SFConfigurationObserver.GetDeployedAppsInfo~System.Threading.Tasks.Task{System.String}")]

[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "<Pending>", Scope = "member", Target = "~M:FabricObserver.AppObserver.#ctor(System.Fabric.StatelessServiceContext,System.Fabric.FabricClient)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "<Pending>", Scope = "member", Target = "~M:FabricObserver.Model.ConfigurationSetting`1.Parse(System.String,System.Type)~System.Object")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "<Pending>", Scope = "member", Target = "~M:FabricObserver.NetworkObserver.#ctor(System.Fabric.StatelessServiceContext,System.Fabric.FabricClient)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "<Pending>", Scope = "member", Target = "~M:FabricObserver.NetworkObserver.ProcessResourceUsageDataReportHealth``1(FabricObserver.Utilities.FabricResourceUsageData{``0},System.String,``0,``0,System.TimeSpan,System.String)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "<Pending>", Scope = "member", Target = "~M:FabricObserver.NodeObserver.#ctor(System.Fabric.StatelessServiceContext,System.Fabric.FabricClient)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "<Pending>", Scope = "member", Target = "~M:FabricObserver.ObserverBase.#ctor(System.String,System.Fabric.StatelessServiceContext,System.Fabric.FabricClient)")]

[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "<Pending>", Scope = "member", Target = "~M:FabricObserver.Utilities.ObserverHealthReporter.#ctor(System.Fabric.FabricClient,FabricObserver.Utilities.Logger)")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "<Pending>", Scope = "member", Target = "~M:FabricObserver.Utilities.DiskUsage.GetCurrentDiskSpaceUsedPercent(System.String)~System.Int32")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Code Quality", "IDE0067:Dispose objects before losing scope", Justification = "<Pending>", Scope = "member", Target = "~M:FabricObserver.ObserverManager.GetObservers~System.Collections.Generic.List{FabricObserver.ObserverBase}")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Code Quality", "IDE0067:Dispose objects before losing scope", Justification = "<Pending>", Scope = "member", Target = "~M:FabricObserver.Utilities.JsonHelper.CreateStreamFromString(System.String)~System.IO.Stream")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "<Pending>", Scope = "member", Target = "~P:FabricObserver.ObserverManager.EtwProviderName")]

