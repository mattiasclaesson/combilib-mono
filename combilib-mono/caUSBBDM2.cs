using System;
using System.Threading;
using System.Diagnostics;
using System.Collections;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using LibUsbDotNet.LibUsb;

namespace combilibmono
{
	public class caUSBBDM2 : caLibUsbAdapter
	{
		class caPacket
		{
			// constants
			public const ushort min_size = 4; // minimum packet size
			
			// dynamic state
			public byte cmd_code;	// command code
			public ushort data_len;	// data block length
			public byte[] data;		// optional data block
			public byte term;		// terminator
		};

		// command terminators
		public const byte term_ack 			= 0x0;
		public const byte term_nack			= 0xff;
		
		public const byte cmd_brd_fwversion = 0x20;
		
		// misc constants
		public const int packet_timeout 	= 1000;
		
		// sync primitives
		private static Mutex cmd_lock = new Mutex();
		private static Mutex response_lock = new Mutex();
		private static Mutex frame_lock = new Mutex();
		private static Mutex flag_lock = new Mutex();
		private static ManualResetEvent response_event = new ManualResetEvent(false);
		private static ManualResetEvent frame_event = new ManualResetEvent(false);

		// dynamic state
		static caPacket in_pack = new caPacket(); // last incomming packet
		static bool pack_incomplete = false;
		static Queue response_queue = new Queue(); // received command responses

		public caUSBBDM2()
		{
			vid = 0xffff;
			pid = 0x0006;
			read_ep_id = ReadEndpointID.Ep02;
			write_ep_id = WriteEndpointID.Ep02;
		}

		// USB connection and adapter management
		public override void Open()
		{
			try
			{
				base.Open();
			} catch (Exception e) {
				Debug.Assert( e != null);
				throw new Exception("Failed to connect to adapter:" + e.Message);
			}
		}

		public override void Close ()
		{
			if (!IsOpen ()) {
				// allready closed
				return;
			}

			// end operation or session
			//if (OperationRunning ())
			//{
			//	end_operation();
			//}

			// close USB connection
			base.Close();
		}

		public override ushort GetFirmwareVersion ()
		{
			try {
				byte[] response = send_command (cmd_brd_fwversion, null, 2, packet_timeout);
				return (ushort)BitConverter.ToInt16(response,0);
			} catch (Exception e) {
				throw new Exception("Failed to get firmware version:" + e.Message);
			}
		}

		void send_command(byte cmd_code , byte[] cmd_data, int timeout)
		{
			byte[] cmd = new byte[cmd_data != null ? caPacket.min_size + cmd_data.Length : caPacket.min_size];
			
			// packet header
			Debug.Assert(cmd != null);
			cmd[0] = cmd_code;
			cmd[1] = (cmd_data != null) ? (byte)(cmd_data.Length >> 8) : (byte)0;
			cmd[2] = (cmd_data != null) ? (byte)cmd_data.Length : (byte)0;
			
			if(cmd_data != null && cmd_data.Length >0)
			{
				// data block
				cmd_data.CopyTo(cmd, 3);
			}
			
			cmd[cmd_data != null ? cmd_data.Length + 3 : 3] = term_ack;
			
			// send command
			write_usb(cmd,timeout);
		}

		byte[] send_command (byte cmd_code, byte[] cmd_data, int reply_data_len, int timeout)
		{
			try {
				cmd_lock.WaitOne();
				
				send_command (cmd_code, cmd_data, timeout);
				return get_response (cmd_code, reply_data_len, timeout);
			} finally {
				cmd_lock.ReleaseMutex();
			}
		}

		static byte[] get_response (byte cmd_code, int reply_data_len, int timeout)
		{
			if (!response_event.WaitOne (timeout)) {
				throw new Exception ("Command timed out");
			}
			
			response_lock.WaitOne();
			
			if (response_queue.Count < 0)
				throw new Exception ("reponse_queue.Count < 0");
			caPacket pack = (caPacket)response_queue.Dequeue ();
			
			if (response_queue.Count < 1) {
				// put threads on hold until new responses appear
				response_event.Reset ();
			}
			
			response_lock.ReleaseMutex();
			
			// check packet contents
			if (pack.cmd_code != cmd_code) {
				// response queue messed up due to packet loss or logic fault
				throw new Exception ("Unexpected response");
			}
			
			if (pack.term != term_ack) {
				// adapter reported command failure due to parametre error in
				// last command or execution failed
				throw new Exception ("Command failed");
			}
			
			if (pack.data_len != reply_data_len) {
				// faulty program logic; packet damage not likely, ince command
				// code and terminator were in place
				throw new Exception("Unexpected response data length");
			}
			
			return (pack.data_len > 0 ? pack.data : null);
		}

		protected override void process_usb ()
		{
			int byte_i;
			
			while (usb_queue.Count >= caPacket.min_size) {
				if (!pack_incomplete)
				{
					// read packet header
					in_pack.cmd_code = (byte)usb_queue.Dequeue();
					in_pack.data_len = 
						(ushort)(((byte)usb_queue.Dequeue() << 8) | (byte)usb_queue.Dequeue());
				}
				
				if (usb_queue.Count <= in_pack.data_len)
				{
					// incomplete packet
					pack_incomplete = true;
					return;
				}
				
				if (in_pack.data_len > 0)
				{
					// read data block
					in_pack.data = new byte[in_pack.data_len];
					if(in_pack.data == null){
						throw new Exception();
					}
					
					for (byte_i = 0; byte_i < in_pack.data_len; ++byte_i)
					{
						in_pack.data[byte_i] = (byte)usb_queue.Dequeue();
					}
				}
				
				// teminator
				in_pack.term = (byte)usb_queue.Dequeue();
				pack_incomplete = false;
				
				response_lock.WaitOne();
				
				// save response packet and notify waiting threads
				response_queue.Enqueue(in_pack);
				response_event.Set();
				
				response_lock.ReleaseMutex();
			}
		}
	}
}

