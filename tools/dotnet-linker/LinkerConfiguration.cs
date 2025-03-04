using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

using Mono.Cecil;
using Mono.Linker;

using Xamarin.Bundler;
using Xamarin.Utils;
using Xamarin.Tuner;

using ObjCRuntime;

namespace Xamarin.Linker {
	public class LinkerConfiguration {
		string LinkerFile;

		public List<Abi> Abis;
		public string AOTCompiler;
		public string AOTOutputDirectory;
		public string CacheDirectory { get; private set; }
		public Version DeploymentTarget { get; private set; }
		public HashSet<string> FrameworkAssemblies { get; private set; } = new HashSet<string> ();
		public string GlobalizationDataFile { get; private set; }
		public string IntermediateLinkDir { get; private set; }
		public bool InvariantGlobalization { get; private set; }
		public string ItemsDirectory { get; private set; }
		public bool IsSimulatorBuild { get; private set; }
		public string PartialStaticRegistrarLibrary { get; set; }
		public ApplePlatform Platform { get; private set; }
		public string PlatformAssembly { get; private set; }
		public string RelativeAppBundlePath { get; private set; }
		public Version SdkVersion { get; private set; }
		public string SdkRootDirectory { get; private set; }
		public int Verbosity => Driver.Verbosity;

		static ConditionalWeakTable<LinkContext, LinkerConfiguration> configurations = new ConditionalWeakTable<LinkContext, LinkerConfiguration> ();

		public Application Application { get; private set; }
		public Target Target { get; private set; }

		public IList<string> RegistrationMethods { get; set; } = new List<string> ();
		public CompilerFlags CompilerFlags;

		public LinkContext Context { get; private set; }
		public DerivedLinkContext DerivedLinkContext { get; private set; }
		public Profile Profile { get; private set; }

		// The list of assemblies is populated in CollectAssembliesStep.
		public List<AssemblyDefinition> Assemblies = new List<AssemblyDefinition> ();
		
		string user_optimize_flags;

		Dictionary<string, List<MSBuildItem>> msbuild_items = new Dictionary<string, List<MSBuildItem>> ();

		public static LinkerConfiguration GetInstance (LinkContext context, bool createIfNotFound = true)
		{
			if (!configurations.TryGetValue (context, out var instance) && createIfNotFound) {
				if (!context.TryGetCustomData ("LinkerOptionsFile", out var linker_options_file))
					throw new Exception ($"No custom linker options file was passed to the linker (using --custom-data LinkerOptionsFile=...");
				instance = new LinkerConfiguration (linker_options_file) {
					Context = context,
				};

				configurations.Add (context, instance);
			}

			return instance;
		}

		LinkerConfiguration (string linker_file)
		{
			if (!File.Exists (linker_file))
				throw new FileNotFoundException ($"The custom linker file {linker_file} does not exist.");

			LinkerFile = linker_file;

			Profile = new BaseProfile (this);
			DerivedLinkContext = new DerivedLinkContext { LinkerConfiguration = this, };
			Application = new Application (this);
			Target = new Target (Application);
			CompilerFlags = new CompilerFlags (Target);

			var use_llvm = false;
			var lines = File.ReadAllLines (linker_file);
			var significantLines = new List<string> (); // This is the input the cache uses to verify if the cache is still valid
			for (var i = 0; i < lines.Length; i++) {
				var line = lines [i].TrimStart ();
				if (line.Length == 0 || line [0] == '#')
					continue; // Allow comments

				var eq = line.IndexOf ('=');
				if (eq == -1)
					throw new InvalidOperationException ($"Invalid syntax for line {i + 1} in {linker_file}: No equals sign.");

				significantLines.Add (line);

				var key = line [..eq];
				var value = line [(eq + 1)..];

				if (string.IsNullOrEmpty (value))
					continue;

				switch (key) {
				case "AreAnyAssembliesTrimmed":
					Application.AreAnyAssembliesTrimmed = string.Equals ("true", value, StringComparison.OrdinalIgnoreCase);
					break;
				case "AssemblyName":
					// This is the AssemblyName MSBuild property for the main project (which is also the root/entry assembly)
					Application.RootAssemblies.Add (value);
					break;
				case "AOTCompiler":
					AOTCompiler = value;
					break;
				case "AOTOutputDirectory":
					AOTOutputDirectory = value;
					break;
				case "CacheDirectory":
					CacheDirectory = value;
					break;
				case "AppBundleManifestPath":
					Application.InfoPListPath = value;
					break;
				case "CustomLinkFlags":
					Application.ParseCustomLinkFlags (value, "gcc_flags");
					break;
				case "Debug":
					Application.EnableDebug = string.Equals (value, "true", StringComparison.OrdinalIgnoreCase);
					break;
				case "DeploymentTarget":
					if (!Version.TryParse (value, out var deployment_target))
						throw new InvalidOperationException ($"Unable to parse the {key} value: {value} in {linker_file}");
					DeploymentTarget = deployment_target;
					break;
				case "Dlsym":
					Application.ParseDlsymOptions (value);
					break;
				case "EnableSGenConc":
					Application.EnableSGenConc = string.Equals (value, "true", StringComparison.OrdinalIgnoreCase);
					break;
				case "EnvironmentVariable":
					var separators = new char [] { ':', '=' };
					var equals = value.IndexOfAny (separators);
					var name = value.Substring (0, equals);
					var val = value.Substring (equals + 1);
					Application.EnvironmentVariables.Add (name, val);
					break;
				case "FrameworkAssembly":
					FrameworkAssemblies.Add (value);
					break;
				case "IntermediateLinkDir":
					IntermediateLinkDir = value;
					break;
				case "Interpreter":
					if (!string.IsNullOrEmpty (value))
						Application.ParseInterpreter (value);
					break;
				case "IsAppExtension":
					Application.IsExtension = string.Equals ("true", value, StringComparison.OrdinalIgnoreCase);
					break;
				case "ItemsDirectory":
					ItemsDirectory = value;
					break;
				case "IsSimulatorBuild":
					IsSimulatorBuild = string.Equals ("true", value, StringComparison.OrdinalIgnoreCase);
					break;
				case "LibMonoLinkMode":
					Application.LibMonoLinkMode = ParseLinkMode (value, key);
					break;
				case "LibXamarinLinkMode":
					Application.LibXamarinLinkMode = ParseLinkMode (value, key);
					break;
				case "MarshalManagedExceptionMode":
					if (!string.IsNullOrEmpty (value)) {
						if (!Application.TryParseManagedExceptionMode (value, out var mode))
							throw new InvalidOperationException ($"Unable to parse the {key} value: {value} in {linker_file}");
						Application.MarshalManagedExceptions = mode;
					}
					break;
				case "MarshalObjectiveCExceptionMode":
					if (!string.IsNullOrEmpty (value)) {
						if (!Application.TryParseObjectiveCExceptionMode (value, out var mode))
							throw new InvalidOperationException ($"Unable to parse the {key} value: {value} in {linker_file}");
						Application.MarshalObjectiveCExceptions = mode;
					}
					break;
				case "MonoLibrary":
					Application.MonoLibraries.Add (value);
					break;
				case "MtouchFloat32":
					if (!TryParseOptionalBoolean (value, out Application.AotFloat32))
						throw new InvalidOperationException ($"Unable to parse the {key} value: {value} in {linker_file}");
					break;
				case "Optimize":
					user_optimize_flags = value;
					break;
				case "PartialStaticRegistrarLibrary":
					PartialStaticRegistrarLibrary = value;
					break;
				case "Platform":
					switch (value) {
					case "iOS":
						Platform = ApplePlatform.iOS;
						break;
					case "tvOS":
						Platform = ApplePlatform.TVOS;
						break;
					case "watchOS":
						Platform = ApplePlatform.WatchOS;
						break;
					case "macOS":
						Platform = ApplePlatform.MacOSX;
						break;
					case "MacCatalyst":
						Platform = ApplePlatform.MacCatalyst;
						break;
					default:
						throw new InvalidOperationException ($"Unknown platform: {value} for the entry {line} in {linker_file}");
					}
					break;
				case "PlatformAssembly":
					PlatformAssembly = Path.GetFileNameWithoutExtension (value);
					break;
				case "RelativeAppBundlePath":
					RelativeAppBundlePath = value;
					break;
				case "Registrar":
					Application.ParseRegistrar (value);
					break;
				case "RuntimeConfigurationFile":
					Application.RuntimeConfigurationFile = value;
					break;
				case "SdkDevPath":
					Driver.SdkRoot = value;
					break;
				case "SdkRootDirectory":
					SdkRootDirectory = value;
					Driver.SetFrameworkCurrentDirectory (value);
					break;
				case "SdkVersion":
					if (!Version.TryParse (value, out var sdk_version))
						throw new InvalidOperationException ($"Unable to parse the {key} value: {value} in {linker_file}");
					SdkVersion = sdk_version;
					break;
				case "TargetArchitectures":
					if (!Enum.TryParse<Abi> (value, out var arch))
						throw new InvalidOperationException ($"Unknown target architectures: {value} in {linker_file}");
					// Add to the list of Abis as separate entries (instead of a flags enum value), because that way it's easier to enumerate over them.
					Abis = new List<Abi> ();
					for (var b = 0; b < 32; b++) {
						var a = (Abi) (1 << b);
						if ((a & arch) == a)
							Abis.Add (a);
					}
					break;
				case "TargetFramework":
					if (!TargetFramework.TryParse (value, out var tf))
						throw new InvalidOperationException ($"Invalid TargetFramework '{value}' in {linker_file}");
					Driver.TargetFramework = TargetFramework.Parse (value);
					break;
				case "UseLlvm":
					use_llvm = string.Equals ("true", value, StringComparison.OrdinalIgnoreCase);
					break;
				case "Verbosity":
					if (!int.TryParse (value, out var verbosity))
						throw new InvalidOperationException ($"Invalid Verbosity '{value}' in {linker_file}");
					Driver.Verbosity += verbosity;
					break;
				case "XamarinRuntime":
					if (!Enum.TryParse<XamarinRuntime> (value, out var rv))
						throw new InvalidOperationException ($"Invalid XamarinRuntime '{value}' in {linker_file}");
					Application.XamarinRuntime = rv;
					break;
				case "GlobalizationDataFile":
					GlobalizationDataFile = value;
					break;
				case "InvariantGlobalization":
					InvariantGlobalization = string.Equals ("true", value, StringComparison.OrdinalIgnoreCase);
					break;
				default:
					throw new InvalidOperationException ($"Unknown key '{key}' in {linker_file}");
				}
			}

			ErrorHelper.Platform = Platform;

			// Optimizations.Parse can only be called after setting ErrorHelper.Platform
			if (!string.IsNullOrEmpty (user_optimize_flags)) {
				var messages = new List<ProductException> ();
				Application.Optimizations.Parse (Application.Platform, user_optimize_flags, messages);
				ErrorHelper.Show (messages);
			}

			if (use_llvm) {
				for (var i = 0; i < Abis.Count; i++) {
					Abis [i] |= Abi.LLVM;
				}
			}

			Application.CreateCache (significantLines.ToArray ());
			Application.Cache.Location = CacheDirectory;
			Application.DeploymentTarget = DeploymentTarget;
			Application.SdkVersion = SdkVersion;
			Application.NativeSdkVersion = SdkVersion;

			DerivedLinkContext.Target = Target;
			Target.Abis = Abis;
			Target.LinkContext = DerivedLinkContext;
			Application.Abis = Abis;

			switch (Platform) {
			case ApplePlatform.iOS:
			case ApplePlatform.TVOS:
			case ApplePlatform.WatchOS:
				Application.BuildTarget = IsSimulatorBuild ? BuildTarget.Simulator : BuildTarget.Device;
				break;
			case ApplePlatform.MacOSX:
			default:
				break;
			}

			if (Driver.TargetFramework.Platform != Platform)
				throw ErrorHelper.CreateError (99, "Inconsistent platforms. TargetFramework={0}, Platform={1}", Driver.TargetFramework.Platform, Platform);

			Driver.ValidateXcode (Application, false, false);

			Application.InitializeCommon ();
			Application.Initialize ();
		}

		bool TryParseOptionalBoolean (string input, out bool? value)
		{
			value = null;

			if (string.IsNullOrEmpty (input))
				return true;

			if (string.Equals (input, "true", StringComparison.OrdinalIgnoreCase)) {
				value = true;
				return true;
			}

			if (string.Equals (input, "false", StringComparison.OrdinalIgnoreCase)) {
				value = false;
				return true;
			}

			return false;
		}

		AssemblyBuildTarget ParseLinkMode (string value, string variableName)
		{
			if (string.Equals (value, "dylib", StringComparison.OrdinalIgnoreCase)) {
				return AssemblyBuildTarget.DynamicLibrary;
			} else if (string.Equals (value, "static", StringComparison.OrdinalIgnoreCase)) {
				return AssemblyBuildTarget.StaticObject;
			} else if (string.Equals (value, "framework", StringComparison.OrdinalIgnoreCase)) {
				return AssemblyBuildTarget.Framework;
			}

			throw new InvalidOperationException ($"Invalid {variableName} '{value}' in {LinkerFile}");
		}

		public void Write ()
		{
			if (Verbosity > 0) {
				Console.WriteLine ($"LinkerConfiguration:");
				Console.WriteLine ($"    ABIs: {string.Join (", ", Abis.Select (v => v.AsArchString ()))}");
				Console.WriteLine ($"    AOTOutputDirectory: {AOTOutputDirectory}");
				Console.WriteLine ($"    AppBundleManifestPath: {Application.InfoPListPath}");
				Console.WriteLine ($"    AreAnyAssembliesTrimmed: {Application.AreAnyAssembliesTrimmed}");
				Console.WriteLine ($"    AssemblyName: {Application.AssemblyName}");
				Console.WriteLine ($"    CacheDirectory: {CacheDirectory}");
				Console.WriteLine ($"    Debug: {Application.EnableDebug}");
				Console.WriteLine ($"    Dlsym: {Application.DlsymOptions} {(Application.DlsymAssemblies != null ? string.Join (" ", Application.DlsymAssemblies.Select (v => (v.Item2 ? "+" : "-") + v.Item1)) : string.Empty)}");
				Console.WriteLine ($"    DeploymentTarget: {DeploymentTarget}");
				Console.WriteLine ($"    EnableSGenConc {Application.EnableSGenConc}");
				Console.WriteLine ($"    IntermediateLinkDir: {IntermediateLinkDir}");
				Console.WriteLine ($"    InterpretedAssemblies: {string.Join (", ", Application.InterpretedAssemblies)}");
				Console.WriteLine ($"    ItemsDirectory: {ItemsDirectory}");
				Console.WriteLine ($"    {FrameworkAssemblies.Count} framework assemblies:");
				foreach (var fw in FrameworkAssemblies.OrderBy (v => v))
					Console.WriteLine ($"        {fw}");
				Console.WriteLine ($"    IsSimulatorBuild: {IsSimulatorBuild}");
				Console.WriteLine ($"    MarshalManagedExceptions: {Application.MarshalManagedExceptions} (IsDefault: {Application.IsDefaultMarshalManagedExceptionMode})");
				Console.WriteLine ($"    MarshalObjectiveCExceptions: {Application.MarshalObjectiveCExceptions}");
				Console.WriteLine ($"    {Application.MonoLibraries.Count} mono libraries:");
				foreach (var lib in Application.MonoLibraries.OrderBy (v => v))
					Console.WriteLine ($"        {lib}");
				Console.WriteLine ($"    Optimize: {user_optimize_flags} => {Application.Optimizations}");
				Console.WriteLine ($"    PartialStaticRegistrarLibrary: {PartialStaticRegistrarLibrary}");
				Console.WriteLine ($"    Platform: {Platform}");
				Console.WriteLine ($"    PlatformAssembly: {PlatformAssembly}.dll");
				Console.WriteLine ($"    RelativeAppBundlePath: {RelativeAppBundlePath}");
				Console.WriteLine ($"    Registrar: {Application.Registrar} (Options: {Application.RegistrarOptions})");
				Console.WriteLine ($"    RuntimeConfigurationFile: {Application.RuntimeConfigurationFile}");
				Console.WriteLine ($"    SdkDevPath: {Driver.SdkRoot}");
				Console.WriteLine ($"    SdkRootDirectory: {SdkRootDirectory}");
				Console.WriteLine ($"    SdkVersion: {SdkVersion}");
				Console.WriteLine ($"    UseInterpreter: {Application.UseInterpreter}");
				Console.WriteLine ($"    UseLlvm: {Application.IsLLVM}");
				Console.WriteLine ($"    Verbosity: {Verbosity}");
				Console.WriteLine ($"    XamarinRuntime: {Application.XamarinRuntime}");
			}
		}

		public string GetAssemblyFileName (AssemblyDefinition assembly)
		{
			return Context.GetAssemblyLocation (assembly);
		}

		public void WriteOutputForMSBuild (string itemName, List<MSBuildItem> items)
		{
			if (!msbuild_items.TryGetValue (itemName, out var list)) {
				msbuild_items [itemName] = items;
			} else {
				list.AddRange (items);
			}
		}

		public void FlushOutputForMSBuild ()
		{
			foreach (var kvp in msbuild_items) {
				var itemName = kvp.Key;
				var items = kvp.Value;

				var xmlNs = XNamespace.Get ("http://schemas.microsoft.com/developer/msbuild/2003");
				var elements = items.Select (item =>
					new XElement (xmlNs + itemName,
						new XAttribute ("Include", item.Include),
							item.Metadata.Select (metadata => new XElement (xmlNs + metadata.Key, metadata.Value))));

				var document = new XDocument (
					new XElement (xmlNs + "Project",
						new XElement (xmlNs + "ItemGroup",
							elements)));

				document.Save (Path.Combine (ItemsDirectory, itemName + ".items"));
			}
		}

		public static void Report (LinkContext Context, params Exception [] exceptions)
		{
			Report (Context, (IList<Exception>) exceptions);
		}

		public static void Report (LinkContext context, IList<Exception> exceptions)
		{
			// We can't really use the linker's reporting facilities and keep our own error codes, because we'll
			// end up re-using the same error codes the linker already uses for its own purposes. So instead show
			// a generic error using the linker's Context.LogMessage API, and then print our own errors to stderr.
			// Since we print using a standard message format, msbuild will parse those error messages and show
			// them as msbuild errors.
			var list = ErrorHelper.CollectExceptions (exceptions);
			var allWarnings = list.All (v => v is ProductException pe && !pe.Error);
			if (!allWarnings) {
				var instance = GetInstance (context, false);
				var platform = (instance?.Platform)?.ToString () ?? "unknown";
				var msg = MessageContainer.CreateCustomErrorMessage (Errors.MX7000 /* An error occured while executing the custom linker steps. Please review the build log for more information. */, 7000, platform);
				context.LogMessage (msg);
			}
			// ErrorHelper.Show will print our errors and warnings to stderr.
			ErrorHelper.Show (list);
		}
	}
}

public class MSBuildItem {
	public string Include;
	public Dictionary<string, string> Metadata = new Dictionary<string, string> ();
}
