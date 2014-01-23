// VncSharp - .NET VNC Client Library
// Copyright (C) 2008 David Humphrey
//
// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation; either version 2 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program; if not, write to the Free Software
// Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.Reflection;
using System.Windows.Forms;
using System.ComponentModel;
using System.Drawing.Imaging;

namespace VncSharp
{
	/// <summary>
	/// Event Handler delegate declaration used by events that signal successful connection with the server.
	/// </summary>
	public delegate void ConnectCompleteHandler(object sender, ConnectEventArgs e);

    /// <summary>
    /// Event Handler delegate used by events that signal failed or lost connection to the server.
    /// </summary>
    public delegate void ConnectionLostHandler(object sender, String connectError);
	
	/// <summary>
	/// When connecting to a VNC Host, a password will sometimes be required.  Therefore a password must be obtained from the user.  A default Password dialog box is included and will be used unless users of the control provide their own Authenticate delegate function for the task.  For example, this might pull a password from a configuration file of some type instead of prompting the user.
	/// </summary>
	public delegate string AuthenticateDelegate();

	/// <summary>
	/// SpecialKeys is a list of the various keyboard combinations that overlap with the client-side and make it
	/// difficult to send remotely.  These values are used in conjunction with the SendSpecialKeys method.
	/// </summary>
	public enum SpecialKeys {
		CtrlAltDel,
		AltF4,
		CtrlEsc, 
		Ctrl,
		Alt
	}

	[ToolboxBitmap(typeof(RemoteDesktop), "Resources.vncviewer.ico")]
	/// <summary>
	/// The RemoteDesktop control takes care of all the necessary RFB Protocol and GUI handling, including mouse and keyboard support, as well as requesting and processing screen updates from the remote VNC host.  Most users will choose to use the RemoteDesktop control alone and not use any of the other protocol classes directly.
	/// </summary>
	public class RemoteDesktop : Panel
	{
	    [Description("Raised after a successful call to the Connect() method.")]
		/// <summary>
		/// Raised after a successful call to the Connect() method.  Includes information for updating the local display in ConnectEventArgs.
		/// </summary>
		public event ConnectCompleteHandler ConnectComplete;
		
		[Description("Raised when the VNC Host drops the connection.")]
		/// <summary>
		/// Raised when the VNC Host drops the connection.
		/// </summary>
		public event ConnectionLostHandler	ConnectionLost;

        [Description("Raised when the VNC Host sends text to the client's clipboard.")]
        /// <summary>
        /// Raised when the VNC Host sends text to the client's clipboard. 
        /// </summary>
        public event EventHandler   ClipboardChanged;

		/// <summary>
		/// Points to a Function capable of obtaining a user's password.  By default this means using the PasswordDialog.GetPassword() function; however, users of RemoteDesktop can replace this with any function they like, so long as it matches the delegate type.
		/// </summary>
		public AuthenticateDelegate GetPassword;
		
		Bitmap desktop;						     // Internal representation of remote image.
		Image  designModeDesktop;			     // Used when painting control in VS.NET designer
		VncClient vnc;						     // The Client object handling all protocol-level interaction
		int port = 5900;					     // The port to connect to on remote host (5900 is default)
		bool passwordPending = false;		     // After Connect() is called, a password might be required.
		bool fullScreenRefresh = false;		     // Whether or not to request the entire remote screen be sent.
        VncDesktopTransformPolicy desktopPolicy;
		RuntimeState state = RuntimeState.Disconnected;

	    private KeyboardHook _keyboardHook = new KeyboardHook();

		private enum RuntimeState {
			Disconnected,
			Disconnecting,
			Connected,
			Connecting
		}
		
		public RemoteDesktop() : base()
		{
            IsShared = false;

			// Since this control will be updated constantly, and all graphics will be drawn by this class,
			// set the control's painting for best user-drawn performance.
			SetStyle(ControlStyles.AllPaintingInWmPaint | 
					 ControlStyles.UserPaint			|
					 ControlStyles.DoubleBuffer			|
                     ControlStyles.Selectable           |   // BUG FIX (Edward Cooke) -- Adding Control.Select() support
					 ControlStyles.ResizeRedraw			|
					 ControlStyles.Opaque,				
					 true);

			// Show a screenshot of a Windows desktop from the manifest and cache to be used when painting in design mode
			designModeDesktop = Image.FromStream(Assembly.GetAssembly(GetType()).GetManifestResourceStream("VncSharp.Resources.screenshot.png"));
			
            // Use a simple desktop policy for design mode.  This will be replaced in Connect()
            desktopPolicy = new VncDesignModeDesktopPolicy(this);
            AutoScroll = desktopPolicy.AutoScroll;
            AutoScrollMinSize = desktopPolicy.AutoScrollMinSize;

			// Users of the control can choose to use their own Authentication GetPassword() method via the delegate above.  This is a default only.
			GetPassword = new AuthenticateDelegate(PasswordDialog.GetPassword);
		}
		
		[DefaultValue(5900)]
		[Description("The port number used by the VNC Host (typically 5900)")]
		/// <summary>
		/// The port number used by the VNC Host (typically 5900).
		/// </summary>
		public int VncPort {
			get { 
				return port; 
			}
			set { 
				// Ignore attempts to use invalid port numbers
				if (value < 1 | value > 65535) value = 5900;
				port = value;	
			}
		}

		/// <summary>
		/// True if the RemoteDesktop is connected and authenticated (if necessary) with a remote VNC Host; otherwise False.
		/// </summary>
		public bool IsConnected {
			get {
				return state == RuntimeState.Connected;
			}
		}
		
		// This is a hack to get around the issue of DesignMode returning
		// false when the control is being removed from a form at design time.
		// First check to see if the control is in DesignMode, then work up 
		// to also check any parent controls.  DesignMode returns False sometimes
		// when it is really True for the parent. Thanks to Claes Bergefall for the idea.
		protected new bool DesignMode {
			get {
				if (base.DesignMode) {
					return true;
				} else {
					Control parent = Parent;
					
					while (parent != null) {
						if (parent.Site != null && parent.Site.DesignMode) {
							return true;
						}
						parent = parent.Parent;
					}
					return false;
				}
			}
		}

		/// <summary>
		/// Returns a more appropriate default size for initial drawing of the control at design time
		/// </summary>
		protected override Size DefaultSize {
			get { 
				return new Size(400, 200);
			}
		}

        [Description("The name of the remote desktop.")]
        /// <summary>
        /// The name of the remote desktop, or "Disconnected" if not connected.
        /// </summary>
        public string Hostname {
            get {
                return vnc == null ? "Disconnected" : vnc.HostName;
            }
        }

        /// <summary>
        /// The image of the remote desktop.
        /// </summary>
        public Image Desktop {
            get {
                return desktop;
            }
        }

		/// <summary>
		/// Get a complete update of the entire screen from the remote host.
		/// </summary>
		/// <remarks>You should allow users to call FullScreenUpdate in order to correct
		/// corruption of the local image.  This will simply request that the next update be
		/// for the full screen, and not a portion of it.  It will not do the update while
		/// blocking.
		/// </remarks>
		/// <exception cref="System.InvalidOperationException">Thrown if the RemoteDesktop control is not in the Connected state.  See <see cref="VncSharp.RemoteDesktop.IsConnected" />.</exception>
		public void FullScreenUpdate()
		{
			InsureConnection(true);
			fullScreenRefresh = true;
		}

        /// <summary>
        /// Controls whether the VNC session should be shared or exclusive.  The default is not shared, which will by default disconnect any
        /// other clients that were connected to a server.  When shared is 'true' the default is to share the desktop among multiple clients.
        /// The default behavior can be overridden by the server configuration.
        /// </summary>
        public bool IsShared { get; set; }

		/// <summary>
		/// Insures the state of the connection to the server, either Connected or Not Connected depending on the value of the connected argument.
		/// </summary>
		/// <param name="connected">True if the connection must be established, otherwise False.</param>
		/// <exception cref="System.InvalidOperationException">Thrown if the RemoteDesktop control is in the wrong state.</exception>
		private void InsureConnection(bool connected)
		{
			// Grab the name of the calling routine:
			string methodName = new System.Diagnostics.StackTrace().GetFrame(1).GetMethod().Name;
			
			if (connected) {
				System.Diagnostics.Debug.Assert(state == RuntimeState.Connected || 
												state == RuntimeState.Disconnecting, // special case for Disconnect()
												string.Format("RemoteDesktop must be in RuntimeState.Connected before calling {0}.", methodName));
				if (state != RuntimeState.Connected && state != RuntimeState.Disconnecting) {
					throw new InvalidOperationException("RemoteDesktop must be in Connected state before calling methods that require an established connection.");
				}
			} else { // disconnected
				System.Diagnostics.Debug.Assert(state == RuntimeState.Disconnected,
												string.Format("RemoteDesktop must be in RuntimeState.Disconnected before calling {0}.", methodName));
                if (state != RuntimeState.Disconnected && state != RuntimeState.Disconnecting) {
					throw new InvalidOperationException("RemoteDesktop cannot be in Connected state when calling methods that establish a connection.");
				}
			}
		}

		// This event handler deals with Frambebuffer Updates coming from the host. An
		// EncodedRectangle object is passed via the VncEventArgs (actually an IDesktopUpdater
		// object so that *only* Draw() can be called here--Decode() is done elsewhere).
		// The VncClient object handles thread marshalling onto the UI thread.
		protected void VncUpdate(object sender, VncEventArgs e)
		{
			e.DesktopUpdater.Draw(desktop);
            Invalidate(desktopPolicy.AdjustUpdateRectangle(e.DesktopUpdater.UpdateRectangle));

			if (state == RuntimeState.Connected) {
				vnc.RequestScreenUpdate(fullScreenRefresh);
				
				// Make sure the next screen update is incremental
    			fullScreenRefresh = false;
			}
		}

        /// <summary>
        /// Connect to a VNC Host and determine whether or not the server requires a password.
        /// </summary>
        /// <param name="host">The IP Address or Host Name of the VNC Host.</param>
        /// <exception cref="System.ArgumentNullException">Thrown if host is null.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown if display is negative.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown if the RemoteDesktop control is already Connected.  See <see cref="VncSharp.RemoteDesktop.IsConnected" />.</exception>
        public void Connect(string host)
        {
            // Use Display 0 by default.
            Connect(host, 0);
        }

        /// <summary>
        /// Connect to a VNC Host and determine whether or not the server requires a password.
        /// </summary>
        /// <param name="host">The IP Address or Host Name of the VNC Host.</param>
        /// <param name="viewOnly">Determines whether mouse and keyboard events will be sent to the host.</param>
        /// <exception cref="System.ArgumentNullException">Thrown if host is null.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown if display is negative.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown if the RemoteDesktop control is already Connected.  See <see cref="VncSharp.RemoteDesktop.IsConnected" />.</exception>
        public void Connect(string host, bool viewOnly)
        {
            // Use Display 0 by default.
            Connect(host, 0, viewOnly);
        }

        /// <summary>
        /// Connect to a VNC Host and determine whether or not the server requires a password.
        /// </summary>
        /// <param name="host">The IP Address or Host Name of the VNC Host.</param>
        /// <param name="viewOnly">Determines whether mouse and keyboard events will be sent to the host.</param>
        /// <param name="scaled">Determines whether to use desktop scaling or leave it normal and clip.</param>
        /// <exception cref="System.ArgumentNullException">Thrown if host is null.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown if display is negative.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown if the RemoteDesktop control is already Connected.  See <see cref="VncSharp.RemoteDesktop.IsConnected" />.</exception>
        public void Connect(string host, bool viewOnly, bool scaled)
        {
            // Use Display 0 by default.
            Connect(host, 0, viewOnly, scaled);
        }

        /// <summary>
        /// Connect to a VNC Host and determine whether or not the server requires a password.
        /// </summary>
        /// <param name="host">The IP Address or Host Name of the VNC Host.</param>
        /// <param name="display">The Display number (used on Unix hosts).</param>
        /// <exception cref="System.ArgumentNullException">Thrown if host is null.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown if display is negative.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown if the RemoteDesktop control is already Connected.  See <see cref="VncSharp.RemoteDesktop.IsConnected" />.</exception>
        public void Connect(string host, int display)
        {
            Connect(host, display, false);
        }

        /// <summary>
        /// Connect to a VNC Host and determine whether or not the server requires a password.
        /// </summary>
        /// <param name="host">The IP Address or Host Name of the VNC Host.</param>
        /// <param name="display">The Display number (used on Unix hosts).</param>
        /// <param name="viewOnly">Determines whether mouse and keyboard events will be sent to the host.</param>
        /// <exception cref="System.ArgumentNullException">Thrown if host is null.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown if display is negative.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown if the RemoteDesktop control is already Connected.  See <see cref="VncSharp.RemoteDesktop.IsConnected" />.</exception>
        public void Connect(string host, int display, bool viewOnly)
        {
            Connect(host, display, viewOnly, false);
        }

        /// <summary>
        /// Connect to a VNC Host and determine whether or not the server requires a password.
        /// </summary>
        /// <param name="host">The IP Address or Host Name of the VNC Host.</param>
        /// <param name="display">The Display number (used on Unix hosts).</param>
        /// <param name="viewOnly">Determines whether mouse and keyboard events will be sent to the host.</param>
        /// <param name="scaled">Determines whether to use desktop scaling or leave it normal and clip.</param>
        /// <exception cref="System.ArgumentNullException">Thrown if host is null.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown if display is negative.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown if the RemoteDesktop control is already Connected.  See <see cref="VncSharp.RemoteDesktop.IsConnected" />.</exception>
        public void Connect(string host, int display, bool viewOnly, bool scaled)
        {
            InsureConnection(false);

            if (host == null) throw new ArgumentNullException("host");
            if (display < 0) throw new ArgumentOutOfRangeException("display", display, "Display number must be a positive integer.");

            // Start protocol-level handling and determine whether a password is needed
            vnc = new VncClient();
            vnc.IsShared = IsShared;
            vnc.ConnectionLost += new VncClient.ConnectionLostHandler(VncClientConnectionLost);
            vnc.ServerCutText += new EventHandler(VncServerCutText);
            vnc.ConnectionOpen += new VncClient.ConnectionOpenedHandler(vnc_ConnectionOpen);
            vnc.Connect(host, display, VncPort, viewOnly);
            SetScalingMode(scaled);
        }

        void vnc_ConnectionOpen(object sender, bool needPassword)
        {
            if (this.InvokeRequired) {
                this.Invoke(new VncClient.ConnectionOpenedHandler(vnc_ConnectionOpen), sender, needPassword);
            } else {
                passwordPending = needPassword;
                if (passwordPending) {
                    // Server needs a password, so call which ever method is refered to by the GetPassword delegate.
                    string password = GetPassword();

                    if (password == null) {
                        // No password could be obtained (e.g., user clicked Cancel), so stop connecting
                        return;
                    } else {
                        Authenticate(password);
                    }
                } else {
                    // No password needed, so go ahead and Initialize here
                    Initialize();
                }
            }
        }

		/// <summary>
		/// Authenticate with the VNC Host using a user supplied password.
		/// </summary>
		/// <exception cref="System.InvalidOperationException">Thrown if the RemoteDesktop control is already Connected.  See <see cref="VncSharp.RemoteDesktop.IsConnected" />.</exception>
		/// <exception cref="System.NullReferenceException">Thrown if the password is null.</exception>
		/// <param name="password">The user's password.</param>
		public void Authenticate(string password)
		{
			InsureConnection(false);
			if (!passwordPending) throw new InvalidOperationException("Authentication is only required when Connect() returns True and the VNC Host requires a password.");
			if (password == null) throw new NullReferenceException("password");

			passwordPending = false;  // repeated calls to Authenticate should fail.
			if (vnc.Authenticate(password)) {
				Initialize();
			} else {		
				OnConnectionLost("Authentication failed");										
			}	
		}

        /// <summary>
        /// Changes the input mode to view-only or interactive.
        /// </summary>
        /// <param name="viewOnly">True if view-only mode is desired (no mouse/keyboard events will be sent).</param>
        public void SetInputMode(bool viewOnly)
        {
            vnc.SetInputMode(viewOnly);
        }

        [DefaultValue(false)]
        [Description("True if view-only mode is desired (no mouse/keyboard events will be sent)")]
        /// <summary>
        /// True if view-only mode is desired (no mouse/keyboard events will be sent).
        /// </summary>
        public bool ViewOnly
        {
            get
            {
                return vnc.IsViewOnly;
            }
            set
            {
                SetInputMode(value);
            }
        }
        
        /// <summary>
        /// Set the remote desktop's scaling mode.
        /// </summary>
        /// <param name="scaled">Determines whether to use desktop scaling or leave it normal and clip.</param>
        public void SetScalingMode(bool scaled)
        {
            if (scaled) {
                desktopPolicy = new VncScaledDesktopPolicy(vnc, this);
            } else {
                desktopPolicy = new VncClippedDesktopPolicy(vnc, this);
            }

            AutoScroll = desktopPolicy.AutoScroll;
            AutoScrollMinSize = desktopPolicy.AutoScrollMinSize;

            Invalidate();
        }

        [DefaultValue(false)]
        [Description("Determines whether to use desktop scaling or leave it normal and clip")]
        /// <summary>
        /// Determines whether to use desktop scaling or leave it normal and clip.
        /// </summary>
        public bool Scaled
        {
            get
            {
                return desktopPolicy.GetType() == typeof(VncScaledDesktopPolicy);
            }
            set
            {
                SetScalingMode(value);
            }
        }

		/// <summary>
		/// After protocol-level initialization and connecting is complete, the local GUI objects have to be set-up, and requests for updates to the remote host begun.
		/// </summary>
		/// <exception cref="System.InvalidOperationException">Thrown if the RemoteDesktop control is already in the Connected state.  See <see cref="VncSharp.RemoteDesktop.IsConnected" />.</exception>		
		protected void Initialize()
		{
		    // Finish protocol handshake with host now that authentication is done.
		    InsureConnection(false);
		    vnc.Initialize();
		    SetState(RuntimeState.Connected);

		    // Create a buffer on which updated rectangles will be drawn and draw a "please wait..." 
		    // message on the buffer for initial display until we start getting rectangles
		    SetupDesktop();

		    // Tell the user of this control the necessary info about the desktop in order to setup the display
		    OnConnectComplete(new ConnectEventArgs(vnc.Framebuffer.Width,
		                                           vnc.Framebuffer.Height,
		                                           vnc.Framebuffer.DesktopName));

		    // Refresh scroll properties
		    AutoScrollMinSize = desktopPolicy.AutoScrollMinSize;

		    // Start getting updates from the remote host (vnc.StartUpdates will begin a worker thread).
		    vnc.VncUpdate += new VncUpdateHandler(VncUpdate);
		    vnc.StartUpdates();

            KeyboardHook.RequestKeyNotification(this.Handle, Win32.VK_LWIN, true);
            KeyboardHook.RequestKeyNotification(this.Handle, Win32.VK_RWIN, true);
            KeyboardHook.RequestKeyNotification(this.Handle, Win32.VK_ESCAPE, KeyboardHook.ModifierKeys.Control, true);
            KeyboardHook.RequestKeyNotification(this.Handle, Win32.VK_TAB, KeyboardHook.ModifierKeys.Alt, true);

            // TODO: figure out why Alt-Shift isn't blocked
            //KeyboardHook.RequestKeyNotification(this.Handle, Win32.VK_SHIFT, KeyboardHook.ModifierKeys.Alt, true);
            //KeyboardHook.RequestKeyNotification(this.Handle, Win32.VK_MENU, KeyboardHook.ModifierKeys.Shift, true);

            // TODO: figure out why PrtScn doesn't work
            //KeyboardHook.RequestKeyNotification(this.Handle, Win32.VK_SNAPSHOT, true);
        }

	    private void SetState(RuntimeState newState)
		{
			state = newState;
			
			// Set mouse pointer according to new state
			switch (state) {
				case RuntimeState.Connected:
					// Change the cursor to the "vnc" custor--a see-through dot
					Cursor = new Cursor(GetType(), "Resources.vnccursor.cur");
					break;
				// All other states should use the normal cursor.
				case RuntimeState.Disconnected:
				default:	
					Cursor = Cursors.Default;				
					break;
			}
		}

		/// <summary>
		/// Creates and initially sets-up the local bitmap that will represent the remote desktop image.
		/// </summary>
		/// <exception cref="System.InvalidOperationException">Thrown if the RemoteDesktop control is not already in the Connected state. See <see cref="VncSharp.RemoteDesktop.IsConnected" />.</exception>
		protected void SetupDesktop()
		{
			InsureConnection(true);

			// Create a new bitmap to cache locally the remote desktop image.  Use the geometry of the
			// remote framebuffer, and 32bpp pixel format (doesn't matter what the server is sending--8,16,
			// or 32--we always draw 32bpp here for efficiency).
			desktop = new Bitmap(vnc.Framebuffer.Width, vnc.Framebuffer.Height, PixelFormat.Format32bppPArgb);
			
			// Draw a "please wait..." message on the local desktop until the first
			// rectangle(s) arrive and overwrite with the desktop image.
			DrawDesktopMessage("Connecting to VNC host, please wait...");
		}
	
		/// <summary>
		/// Draws the given message (white text) on the local desktop (all black).
		/// </summary>
		/// <param name="message">The message to be drawn.</param>
		protected void DrawDesktopMessage(string message)
		{
			System.Diagnostics.Debug.Assert(desktop != null, "Can't draw on desktop when null.");
			// Draw the given message on the local desktop
			using (Graphics g = Graphics.FromImage(desktop)) {
				g.FillRectangle(Brushes.Black, vnc.Framebuffer.Rectangle);

				StringFormat format = new StringFormat();
				format.Alignment = StringAlignment.Center;
				format.LineAlignment = StringAlignment.Center;

				g.DrawString(message, 
							 new Font("Arial", 12), 
							 new SolidBrush(Color.White), 
							 new PointF(vnc.Framebuffer.Width / 2, vnc.Framebuffer.Height / 2), format);
			}

		}
		
		/// <summary>
		/// Stops the remote host from sending further updates and disconnects.
		/// </summary>
		/// <exception cref="System.InvalidOperationException">Thrown if the RemoteDesktop control is not already in the Connected state. See <see cref="VncSharp.RemoteDesktop.IsConnected" />.</exception>
		public void Disconnect()
		{
			InsureConnection(true);
			vnc.ConnectionLost -= new VncClient.ConnectionLostHandler(VncClientConnectionLost);
            vnc.ServerCutText -= new EventHandler(VncServerCutText);
			vnc.Disconnect();
			SetState(RuntimeState.Disconnected);
			OnConnectionLost(null);
			Invalidate();
		}

        /// <summary>
        /// Fills the remote server's clipboard with the text in the client's clipboard, if any.
        /// </summary>
        public void FillServerClipboard()
        {
            FillServerClipboard(Clipboard.GetText());
        }

        /// <summary>
        /// Fills the remote server's clipboard with text.
        /// </summary>
        /// <param name="text">The text to put in the server's clipboard.</param>
        public void FillServerClipboard(string text)
        {
            vnc.WriteClientCutText(text);
        }

		protected override void Dispose(bool disposing)
		{
			if (disposing) {
				// Make sure the connection is closed--should never happen :)
				if (state != RuntimeState.Disconnected) {
					Disconnect();
				}

				// See if either of the bitmaps used need clean-up.  
				if (desktop != null) desktop.Dispose();
				if (designModeDesktop != null) designModeDesktop.Dispose();
			}
			base.Dispose(disposing);
		}

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == KeyboardHook.HookKeyMsg)
            {
                var msgData = (KeyboardHook.HookKeyMsgData)Marshal.PtrToStructure(m.LParam, typeof(KeyboardHook.HookKeyMsgData));
                HandleKeyboardEvent(m.WParam.ToInt32(), msgData.KeyCode, msgData.ModifierKeys);
            }
            else
                base.WndProc(ref m);
        }

		protected override void OnPaint(PaintEventArgs pe)
		{
			// If the control is in design mode, draw a nice background, otherwise paint the desktop.
			if (!DesignMode) {
				switch(state) {
					case RuntimeState.Connected:
						//System.Diagnostics.Debug.Assert(desktop != null);
                        // Assertion fails when OnPaint is called before the bitmap has been constructed.  Here we skip the paint
                        // and hope that the screen will be filled in later.
                        if (desktop != null)
                        {
                            DrawDesktopImage(desktop, pe.Graphics);
                        }
						break;
					case RuntimeState.Disconnected:
						// Do nothing, just black background.
						break;
					default:
						// Sanity check
						throw new NotImplementedException(string.Format("RemoteDesktop in unknown State: {0}.", state.ToString()));
				}
            } else {
				// Draw a static screenshot of a Windows desktop to simulate the control in action
				System.Diagnostics.Debug.Assert(designModeDesktop != null);
				DrawDesktopImage(designModeDesktop, pe.Graphics);
			}
			base.OnPaint(pe);
		}

        protected override void OnResize(EventArgs eventargs)
        {
            // Fix a bug with a ghost scrollbar in clipped mode on maximize
            Control parent = Parent;
            while (parent != null) {
                if (parent is Form) {
                    Form form = parent as Form;
                    if (form.WindowState == FormWindowState.Maximized)
                        form.Invalidate();
                    parent = null;
                } else {
                    parent = parent.Parent;
                }
            }

            base.OnResize(eventargs);
        }

		/// <summary>
		/// Draws an image onto the control in a size-aware way.
		/// </summary>
		/// <param name="desktopImage">The desktop image to be drawn to the control's sufrace.</param>
		/// <param name="g">The Graphics object representing the control's drawable surface.</param>
		/// <exception cref="System.InvalidOperationException">Thrown if the RemoteDesktop control is not already in the Connected state.</exception>
		protected void DrawDesktopImage(Image desktopImage, Graphics g)
		{
			g.DrawImage(desktopImage, desktopPolicy.RepositionImage(desktopImage));
		}

		/// <summary>
		/// RemoteDesktop listens for ConnectionLost events from the VncClient object.
		/// </summary>
		/// <param name="sender">The VncClient object that raised the event.</param>
		/// <param name="e">An empty EventArgs object.</param>
		protected void VncClientConnectionLost(object sender, string errorString)
		{
			// If the remote host dies, and there are attempts to write
			// keyboard/mouse/update notifications, this may get called 
			// many times, and from main or worker thread.
			// Guard against this and invoke Disconnect once.
            if (state == RuntimeState.Connected) {
                SetState(RuntimeState.Disconnecting);
                Disconnect();
            } else {
                OnConnectionLost(errorString);
            }
		}

        // Handle the VncClient ServerCutText event and bubble it up as ClipboardChanged.
        protected void VncServerCutText(object sender, EventArgs e)
        {
            OnClipboardChanged();
        }

        protected void OnClipboardChanged()
        {
            if (ClipboardChanged != null)
                ClipboardChanged(this, EventArgs.Empty);
        }

		/// <summary>
		/// Dispatches the ConnectionLost event if any targets have registered.
		/// </summary>
		/// <param name="e">An EventArgs object.</param>
		/// <exception cref="System.InvalidOperationException">Thrown if the RemoteDesktop control is in the Connected state.</exception>
		protected void OnConnectionLost(string errorString)
		{
			if (ConnectionLost != null) {
				ConnectionLost(this, errorString);
			}
		}
		
		/// <summary>
		/// Dispatches the ConnectComplete event if any targets have registered.
		/// </summary>
		/// <param name="e">A ConnectEventArgs object with information about the remote framebuffer's geometry.</param>
		/// <exception cref="System.InvalidOperationException">Thrown if the RemoteDesktop control is not in the Connected state.</exception>
		protected void OnConnectComplete(ConnectEventArgs e)
		{
			if (ConnectComplete != null) {
				ConnectComplete(this, e);
			}
		}

		// Handle Mouse Events:		 -------------------------------------------
		// In all cases, the same thing needs to happen: figure out where the cursor
		// is, and then figure out the state of all mouse buttons.
		// TODO: currently we don't handle the case of 3-button emulation with 2-buttons.
		protected override void OnMouseMove(MouseEventArgs mea)
		{
			UpdateRemotePointer();
			base.OnMouseMove(mea);
		}

		protected override void OnMouseDown(MouseEventArgs mea)
		{
            // BUG FIX (Edward Cooke) -- Deal with Control.Select() semantics
            if (!Focused) {
                Focus();
                Select();
            } else {
                UpdateRemotePointer();
            }
			base.OnMouseDown(mea);
		}
		
		// Find out the proper masks for Mouse Button Up Events
		protected override void OnMouseUp(MouseEventArgs mea)
		{
   			UpdateRemotePointer();
			base.OnMouseUp(mea);
		}
		
		// TODO: Perhaps overload UpdateRemotePointer to take a flag indicating if mousescroll has occured??
		protected override void OnMouseWheel(MouseEventArgs mea)
		{
			// HACK: this check insures that while in DesignMode, no messages are sent to a VNC Host
			// (i.e., there won't be one--NullReferenceException)			
            if (!DesignMode && IsConnected) {
				Point current = PointToClient(MousePosition);
				byte mask = 0;

				// mouse was scrolled forward
				if (mea.Delta > 0) {
					mask += 8;
				} else if (mea.Delta < 0) { // mouse was scrolled backwards
					mask += 16;
				}

				vnc.WritePointerEvent(mask, desktopPolicy.GetMouseMovePoint(current));
			}			
			base.OnMouseWheel(mea);
		}
		
		private void UpdateRemotePointer()
		{
			// HACK: this check insures that while in DesignMode, no messages are sent to a VNC Host
			// (i.e., there won't be one--NullReferenceException)			
			if (!DesignMode && IsConnected) {
				Point current = PointToClient(MousePosition);
				byte mask = 0;

				if (Control.MouseButtons == MouseButtons.Left)   mask += 1;
				if (Control.MouseButtons == MouseButtons.Middle) mask += 2;
				if (Control.MouseButtons == MouseButtons.Right)  mask += 4;

                Rectangle adjusted = desktopPolicy.GetMouseMoveRectangle();
                if (adjusted.Contains(current))
                    vnc.WritePointerEvent(mask, desktopPolicy.UpdateRemotePointer(current));
			}
		}

        [SecurityPermissionAttribute(SecurityAction.LinkDemand, Flags = SecurityPermissionFlag.UnmanagedCode)]
        [SecurityPermissionAttribute(SecurityAction.InheritanceDemand, Flags = SecurityPermissionFlag.UnmanagedCode)]
        protected override bool ProcessKeyEventArgs(ref Message m)
        {
            return HandleKeyboardEvent(m.Msg, m.WParam.ToInt32(), KeyboardHook.GetModifierKeyState());
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            return ProcessKeyEventArgs(ref msg);
        }

        protected static Dictionary<Int32, Int32> KeyTranslationTable = new Dictionary<Int32, Int32>
        {
            { Win32.VK_CANCEL, RfbProtocol.XK_Cancel },
            { Win32.VK_BACK, RfbProtocol.XK_BackSpace },
            { Win32.VK_TAB, RfbProtocol.XK_Tab },
            { Win32.VK_CLEAR, RfbProtocol.XK_Clear },
            { Win32.VK_RETURN, RfbProtocol.XK_Return },
            { Win32.VK_PAUSE, RfbProtocol.XK_Pause },
            { Win32.VK_ESCAPE, RfbProtocol.XK_Escape },
            { Win32.VK_SNAPSHOT, RfbProtocol.XK_Sys_Req },
            { Win32.VK_INSERT, RfbProtocol.XK_Insert },
            { Win32.VK_DELETE, RfbProtocol.XK_Delete },
            { Win32.VK_HOME, RfbProtocol.XK_Home },
            { Win32.VK_END, RfbProtocol.XK_End },
            { Win32.VK_PRIOR, RfbProtocol.XK_Prior }, // Page Up
            { Win32.VK_NEXT, RfbProtocol.XK_Next }, // Page Down
            { Win32.VK_LEFT, RfbProtocol.XK_Left },
            { Win32.VK_UP, RfbProtocol.XK_Up },
            { Win32.VK_RIGHT, RfbProtocol.XK_Right },
            { Win32.VK_DOWN, RfbProtocol.XK_Down },
            { Win32.VK_SELECT, RfbProtocol.XK_Select },
            { Win32.VK_PRINT, RfbProtocol.XK_Print },
            { Win32.VK_EXECUTE, RfbProtocol.XK_Execute },
            { Win32.VK_HELP, RfbProtocol.XK_Help },
            { Win32.VK_F1, RfbProtocol.XK_F1 },
            { Win32.VK_F2, RfbProtocol.XK_F2 },
            { Win32.VK_F3, RfbProtocol.XK_F3 },
            { Win32.VK_F4, RfbProtocol.XK_F4 },
            { Win32.VK_F5, RfbProtocol.XK_F5 },
            { Win32.VK_F6, RfbProtocol.XK_F6 },
            { Win32.VK_F7, RfbProtocol.XK_F7 },
            { Win32.VK_F8, RfbProtocol.XK_F8 },
            { Win32.VK_F9, RfbProtocol.XK_F9 },
            { Win32.VK_F10, RfbProtocol.XK_F10 },
            { Win32.VK_F11, RfbProtocol.XK_F11 },
            { Win32.VK_F12, RfbProtocol.XK_F12 },
            { Win32.VK_APPS, RfbProtocol.XK_Menu },
        };

        public static Int32 TranslateVirtualKey(Int32 virtualKey, KeyboardHook.ModifierKeys modifierKeys)
        {
            if (KeyTranslationTable.ContainsKey(virtualKey))
                return KeyTranslationTable[virtualKey];

            // Windows sends the uppercase letter when the user presses a hotkey
            // like Ctrl-A. ToAscii takes into effect the keyboard layout and
            // state of the modifier keys. This will give us the lowercase letter
            // unless the user is also pressing Shift.
            var keyboardState = new byte[256];
            if (!Win32.GetKeyboardState(keyboardState))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            keyboardState[Win32.VK_CONTROL] = 0;
            keyboardState[Win32.VK_LCONTROL] = 0;
            keyboardState[Win32.VK_RCONTROL] = 0;
            keyboardState[Win32.VK_MENU] = 0;
            keyboardState[Win32.VK_LMENU] = 0;
            keyboardState[Win32.VK_RMENU] = 0;
            keyboardState[Win32.VK_LWIN] = 0;
            keyboardState[Win32.VK_RWIN] = 0;

            var charResult = new byte[2];
            var charCount = Win32.ToAscii(virtualKey, Win32.MapVirtualKey(virtualKey, 0), keyboardState, charResult, 0);

            // TODO: This could probably be handled better. For now, we'll just return the last character.
            if (charCount > 0) return Convert.ToInt32(charResult[charCount - 1]);

            return virtualKey;
        }

        public static Boolean IsModifierKey(Int32 keyCode)
        {
            switch (keyCode)
            {
                case Win32.VK_SHIFT:
                case Win32.VK_LSHIFT:
                case Win32.VK_RSHIFT:
                case Win32.VK_CONTROL:
                case Win32.VK_LCONTROL:
                case Win32.VK_RCONTROL:
                case Win32.VK_MENU:
                case Win32.VK_LMENU:
                case Win32.VK_RMENU:
                case Win32.VK_LWIN:
                case Win32.VK_RWIN:
                    return true;
                default:
                    return false;
            }
        }

	    protected KeyboardHook.ModifierKeys PreviousModifierKeyState;

        protected void SyncModifierKeyState(KeyboardHook.ModifierKeys modifierKeys)
        {
            if ((PreviousModifierKeyState & KeyboardHook.ModifierKeys.LeftShift) !=
                (modifierKeys & KeyboardHook.ModifierKeys.LeftShift))
                vnc.WriteKeyboardEvent(RfbProtocol.XK_Shift_L, (modifierKeys & KeyboardHook.ModifierKeys.LeftShift) != 0);
            if ((PreviousModifierKeyState & KeyboardHook.ModifierKeys.RightShift) !=
                (modifierKeys & KeyboardHook.ModifierKeys.RightShift))
                vnc.WriteKeyboardEvent(RfbProtocol.XK_Shift_R, (modifierKeys & KeyboardHook.ModifierKeys.RightShift) != 0);

            if ((PreviousModifierKeyState & KeyboardHook.ModifierKeys.LeftControl) !=
                (modifierKeys & KeyboardHook.ModifierKeys.LeftControl))
                vnc.WriteKeyboardEvent(RfbProtocol.XK_Control_L, (modifierKeys & KeyboardHook.ModifierKeys.LeftControl) != 0);
            if ((PreviousModifierKeyState & KeyboardHook.ModifierKeys.RightControl) !=
                (modifierKeys & KeyboardHook.ModifierKeys.RightControl))
                vnc.WriteKeyboardEvent(RfbProtocol.XK_Control_R, (modifierKeys & KeyboardHook.ModifierKeys.RightControl) != 0);

            if ((PreviousModifierKeyState & KeyboardHook.ModifierKeys.LeftAlt) !=
                (modifierKeys & KeyboardHook.ModifierKeys.LeftAlt))
                vnc.WriteKeyboardEvent(RfbProtocol.XK_Alt_L, (modifierKeys & KeyboardHook.ModifierKeys.LeftAlt) != 0);
            if ((PreviousModifierKeyState & KeyboardHook.ModifierKeys.RightAlt) !=
                (modifierKeys & KeyboardHook.ModifierKeys.RightAlt))
                vnc.WriteKeyboardEvent(RfbProtocol.XK_Alt_R, (modifierKeys & KeyboardHook.ModifierKeys.RightAlt) != 0);

            if ((PreviousModifierKeyState & KeyboardHook.ModifierKeys.LeftWin) !=
                (modifierKeys & KeyboardHook.ModifierKeys.LeftWin))
                vnc.WriteKeyboardEvent(RfbProtocol.XK_Super_L, (modifierKeys & KeyboardHook.ModifierKeys.LeftWin) != 0);
            if ((PreviousModifierKeyState & KeyboardHook.ModifierKeys.RightWin) !=
                (modifierKeys & KeyboardHook.ModifierKeys.RightWin))
                vnc.WriteKeyboardEvent(RfbProtocol.XK_Super_R, (modifierKeys & KeyboardHook.ModifierKeys.RightWin) != 0);

            PreviousModifierKeyState = modifierKeys;
        }

        protected bool HandleKeyboardEvent(Int32 msg, Int32 virtualKey, KeyboardHook.ModifierKeys modifierKeys)
        {
            if (DesignMode || !IsConnected)
                return false;

            if (modifierKeys != PreviousModifierKeyState)
                SyncModifierKeyState(modifierKeys);

            if (IsModifierKey(virtualKey)) return true;

            Boolean pressed;
            switch (msg)
            {
                case Win32.WM_KEYDOWN:
                case Win32.WM_SYSKEYDOWN:
                    pressed = true;
                    break;
                case Win32.WM_KEYUP:
                case Win32.WM_SYSKEYUP:
                    pressed = false;
                    break;
                default:
                    return false;
            }

            vnc.WriteKeyboardEvent(Convert.ToUInt32(TranslateVirtualKey(virtualKey, modifierKeys)), pressed);

            return true;
        }

		/// <summary>
		/// Sends a keyboard combination that would otherwise be reserved for the client PC.
		/// </summary>
		/// <param name="keys">SpecialKeys is an enumerated list of supported keyboard combinations.</param>
		/// <remarks>Keyboard combinations are Pressed and then Released, while single keys (e.g., SpecialKeys.Ctrl) are only pressed so that subsequent keys will be modified.</remarks>
		/// <exception cref="System.InvalidOperationException">Thrown if the RemoteDesktop control is not in the Connected state.</exception>
		public void SendSpecialKeys(SpecialKeys keys)
		{
			this.SendSpecialKeys(keys, true);
		}

		/// <summary>
		/// Sends a keyboard combination that would otherwise be reserved for the client PC.
		/// </summary>
		/// <param name="keys">SpecialKeys is an enumerated list of supported keyboard combinations.</param>
		/// <remarks>Keyboard combinations are Pressed and then Released, while single keys (e.g., SpecialKeys.Ctrl) are only pressed so that subsequent keys will be modified.</remarks>
		/// <exception cref="System.InvalidOperationException">Thrown if the RemoteDesktop control is not in the Connected state.</exception>
		public void SendSpecialKeys(SpecialKeys keys, bool release)
		{
			InsureConnection(true);
			// For all of these I am sending the key presses manually instead of calling
			// the keyboard event handlers, as I don't want to propegate the calls up to the 
			// base control class and form.
			switch(keys) {
				case SpecialKeys.Ctrl:
					PressKeys(new uint[] { 0xffe3 }, release);	// CTRL, but don't release
					break;
				case SpecialKeys.Alt:
					PressKeys(new uint[] { 0xffe9 }, release);	// ALT, but don't release
					break;
				case SpecialKeys.CtrlAltDel:
					PressKeys(new uint[] { 0xffe3, 0xffe9, 0xffff }, release); // CTRL, ALT, DEL
					break;
				case SpecialKeys.AltF4:
					PressKeys(new uint[] { 0xffe9, 0xffc1 }, release); // ALT, F4
					break;					
				case SpecialKeys.CtrlEsc:
					PressKeys(new uint[] { 0xffe3, 0xff1b }, release); // CTRL, ESC
					break;
				// TODO: are there more I should support???
				default:
					break;
			}
		}
		
		/// <summary>
		/// Given a list of keysym values, sends a key press for each, then a release.
		/// </summary>
		/// <param name="keys">An array of keysym values representing keys to press/release.</param>
		/// <param name="release">A boolean indicating whether the keys should be Pressed and then Released.</param>
		private void PressKeys(uint[] keys, bool release)
		{
			System.Diagnostics.Debug.Assert(keys != null, "keys[] cannot be null.");
			
			for(int i = 0; i < keys.Length; ++i) {
				vnc.WriteKeyboardEvent(keys[i], true);
			}
			
			if (release) {
				// Walk the keys array backwards in order to release keys in correct order
				for(int i = keys.Length - 1; i >= 0; --i) {
					vnc.WriteKeyboardEvent(keys[i], false);
				}
			}
		}
	}
}