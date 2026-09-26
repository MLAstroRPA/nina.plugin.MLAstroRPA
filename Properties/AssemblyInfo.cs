using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is the PluginId (Identifier) of the MLAstroRPA plugin.
// NOTE: this is a UNIQUE GUID - distinct from the standalone TPPA plugin
// (1de8d7d3-f11e-494c-a371-95cb48dffa18) AND from the merged MLAstroRPA+TPPA plugin
// (1352D162-2E66-4F80-A05B-854F021DB913), so NINA treats all of them as separate plugins that can
// be installed side by side.
[assembly: Guid("af3ab7b3-f11a-4671-a87f-e7c3985fb509")]

//The minimum Version of N.I.N.A. that this plugin is compatible with
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.1.2.9001")]

//Your plugin homepage - omit if not applicable
[assembly: AssemblyMetadata("Homepage", "https://github.com/MLAstroRPA/nina.plugin.MLAstroRPA")]
//The license your plugin code is using
[assembly: AssemblyMetadata("License", "MPL-2.0")]
//The url to the license
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
//The repository where your plugin is hosted
[assembly: AssemblyMetadata("Repository", "https://github.com/MLAstroRPA/nina.plugin.MLAstroRPA")]

[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/MLAstroRPA/nina.plugin.MLAstroRPA/blob/main/Changelog.md")]

//Common tags that quickly describe your plugin
[assembly: AssemblyMetadata("Tags", "Polar alignment,Motor Control,Hardware,MLAstroRPA")]

[assembly: AssemblyMetadata("FeaturedImageURL", "https://raw.githubusercontent.com/MLAstroRPA/nina.plugin.MLAstroRPA/main/MLAstroRPA-navigation/Resources/MLAstro_logo.png")]

[assembly: AssemblyMetadata("LongDescription", @"MLAstro Robotic Polar Alignment control from inside N.I.N.A.

This plugin is the hardware side of the MLAstro Robotic Polar Alignment system. It talks to the
MLAstro RPA controller over USB serial or Wi-Fi, jogs the altitude and azimuth axes by hand or by a
set number of degrees, and shows live position, status and alarms. It also keeps the controller's own
settings: motor drivers, soft limits, backlash, Wi-Fi and the P.A. overshoot used during a correction.

The whole controller lives in four tabs: CONTROL for the jog pad, position readout and STOP / FORCE
STOP, HARDWARE SETTING for the controller parameters, SOFTWARE SETTING for the correction options and
the broker log, and CONNECTION for the serial port or wireless link, the handshake and the on-board
terminal.

For automatic correction, install the Three Point Polar Alignment plugin. TPPA measures the polar
error and this plugin then moves the axes through the NINA message broker, honouring pause, stop and
cancel.

Prerequisites
* A supported MLAstro RPA controller, connected over USB serial or Wi-Fi
* N.I.N.A. 3.1.2 or newer
* For automatic correction with TPPA: the Three Point Polar Alignment plugin, a camera, plate solving
  and a mount whose right ascension axis can be moved

For best results, keep the controller connected in one plugin only: the port and the controller
firmware accept a single session at a time.")]
