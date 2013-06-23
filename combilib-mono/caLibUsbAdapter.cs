using System;
using System.Threading;
using System.Diagnostics;
using System.Collections;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using LibUsbDotNet.LibUsb;

namespace combilibmono
{
	public abstract class caLibUsbAdapter : caAdapterBase
	{
		// constants
		const uint boot_transfer_size = 4;

		private UsbDeviceFinder usb_finder;
		static caLibUsbAdapter thisptr;

		// sync primitives
		private static Mutex write_lock = new Mutex();
		private static Mutex read_lock = new Mutex();
		private static AutoResetEvent read_event = new AutoResetEvent(false);

		// dynamic state
		public UsbDevice usb_device;
		public IUsbDevice wholeUsbDevice;
		public UsbEndpointReader ep_reader;
		public UsbEndpointWriter ep_writer;
		bool boot_mode;

		// dynamic state
		protected int vid;
		protected int pid;
		protected ReadEndpointID read_ep_id;
		protected WriteEndpointID write_ep_id;
		protected Queue usb_queue;

		public caLibUsbAdapter()
		{
			thisptr = this;
			boot_mode = false;
			usb_queue = new Queue();
			Debug.Assert (usb_queue != null);
		}

		// USB connection and adapter management
		public override void Open()
		{
			this.Close();

			try
			{
				caLibUsbAdapter.write_lock.WaitOne();

				usb_finder = new UsbDeviceFinder(this.vid, this.pid);
				Debug.Assert(this.usb_finder != null);

				// open device
				usb_device = UsbDevice.OpenUsbDevice(usb_finder);
				if (usb_device == null)
				{
					throw new Exception("No compatible adapters found");
				}

				wholeUsbDevice = usb_device as IUsbDevice;
				if (!ReferenceEquals (wholeUsbDevice, null)) {
					wholeUsbDevice.SetConfiguration(1);
					wholeUsbDevice.ClaimInterface(1);
				} else {
					throw new Exception("Failed to claim interface");
				}

				// open endpoints
				ep_reader = usb_device.OpenEndpointReader(this.read_ep_id);
				ep_writer = usb_device.OpenEndpointWriter(this.write_ep_id);
				if(ep_reader == null || ep_writer == null)
				{
					throw new Exception("Failed to open endpoints");
				}

				// clear garbage from input
				this.ep_reader.ReadFlush();

				// attach read event
				ep_reader.DataReceived += (read_usb);
				ep_reader.DataReceivedEnabled = true;

			} catch (Exception e) {
				this.Close();
				throw e;
			} finally {
				caLibUsbAdapter.write_lock.ReleaseMutex();
			}

		}

		public override bool IsOpen()
		{
			write_lock.WaitOne();
			bool ret = (usb_device != null && usb_device.IsOpen);
			write_lock.ReleaseMutex();
			return ret;
		}

		public override void Close ()
		{
			try {
				write_lock.WaitOne ();

				if (ep_reader != null) {
					// detach read event
					ep_reader.DataReceivedEnabled = false;
					ep_reader.DataReceived -= (read_usb);
				}

				ep_reader = null;
				ep_writer = null;

				if (IsOpen ()) {
					// close devices
					usb_device.Close ();
					wholeUsbDevice.ReleaseInterface (1);
					wholeUsbDevice.Close ();
				}

				// release devices
				usb_device = null;
				wholeUsbDevice = null;
				UsbDevice.Exit();
			} catch (Exception) {
				// Ignore everything
			} finally {
				write_lock.ReleaseMutex();
			}

		}

		protected bool in_boot_mode ()
		{
			return boot_mode;
		}

		protected void write_usb (byte[] buf, int timeout)
		{
			if (!IsOpen()) {
				// fail silently
				return;
			}

			try {
				write_lock.WaitOne();

				// write to device
				Debug.Assert(ep_writer != null);
				int bytes_written;

				if (ep_writer.Write (buf, timeout, out bytes_written) != ErrorCode.None || 
					bytes_written != buf.Length) {
					throw new Exception ("Failed to write to adapter");
				}
			}

			// clean up
			finally
			{
				write_lock.ReleaseMutex();
			}
		}

		protected static void read_usb (object sender, EndpointDataEventArgs e)
		{
			Debug.Assert(e != null && e.Buffer != null);
			caLibUsbAdapter.read_lock.WaitOne();

			if(e.Count > 0)
			{
				// add to queue; NB: incomming buffer is fixed-sizem so .ForEach()
				// cannot be used
				for(int i = 0;i < e.Count; i++)
				{
						thisptr.usb_queue.Enqueue(e.Buffer[i]);
				}
					
				if(thisptr.boot_mode)
				{
					caLibUsbAdapter.read_event.Set();
				}
				else
				{
					thisptr.process_usb();
				}
			}

			caLibUsbAdapter.read_lock.ReleaseMutex();
		}

		protected abstract void process_usb();
	}
}

