using System;
using System.Threading;
using System.Diagnostics;
using System.Collections;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using LibUsbDotNet.LibUsb;
using System.IO;

namespace combilibmono
{
	public class caCombiAdapter : caLibUsbAdapter
	{
		public class caCANFrame
		{
			public uint id;
			public byte length;
			public UInt64 data;
			public byte is_extended;
			public byte is_remote;
		};

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
		public const byte term_ack				= 0x0;
		public const byte term_nack				= 0xff;
		
		public const byte cmd_brd_fwversion		= 0x20;
		public const byte cmd_brd_adcfilter		= 0x21;
		public const byte cmd_brd_adc			= 0x22;
		public const byte cmd_brd_egt			= 0x23;

		public const byte cmd_can_open			= 0x80;
		public const byte cmd_can_bitrate		= 0x81;
		public const byte cmd_can_frame			= 0x82;
		public const byte cmd_can_txframe		= 0x83;

		public const byte cmd_can_ecuconnect	= 0x89;
		public const byte cmd_can_readflash		= 0x8a;
		public const byte cmd_can_writeflash	= 0x8b;
		
		// misc constants
		public const int packet_timeout			= 1000;
		public const ushort transfer_block_size	= 256;
		public const uint adc_num_channels		= 5;
		
		// sync primitives
		private static Mutex cmd_lock = new Mutex();
		private static Mutex response_lock = new Mutex();
		private static Mutex frame_lock = new Mutex();
		private static Mutex flag_lock = new Mutex();
		private static ManualResetEvent response_event = new ManualResetEvent(false);
		private static ManualResetEvent frame_event = new ManualResetEvent(false);

		// dynamic state
		static caPacket in_pack = new caPacket();	// last incomming packet
		static bool pack_incomplete = false;		// incomplete packet flag
		static Queue response_queue = new Queue();	// received command responses
		static Queue frame_queue = new Queue();		// received CAN frames
		byte flash_method;							// flashing method
		UInt32[] table = new UInt32[256];

		public caCombiAdapter()
		{
			vid = 0xffff;
			pid = 0x0005;
			read_ep_id = ReadEndpointID.Ep02;
			write_ep_id = WriteEndpointID.Ep05;
		}

		// USB connection and adapter management
		public override void Open()
		{
			try
			{
				// open device
				base.Open();

				if(!in_boot_mode())
				{
					// close CAN channel
					CAN_Open(false);
				}

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
			if (OperationRunning ())
			{
				end_operation();
			}

			if(!in_boot_mode())
			{
				// close CAN channel
				CAN_DisconnectECU(false);
				CAN_Open(false);
			}

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

		public bool GetADCFiltering(uint channel)
		{
			try {
				if (channel >= adc_num_channels)
				{
					throw new Exception("Unknown channel number");
				}

				byte[] cmd_data = {(byte)channel};
				byte[] response = send_command (cmd_brd_adcfilter, cmd_data, 1, packet_timeout);
				return (response [0] == 0x1);
			} catch (Exception e) {
				throw new Exception("Failed to get A/D filter settings:" + e.Message);
			}
		}

		public void SetADCFiltering(uint channel, bool enable)
		{
			try {
				if (channel >= adc_num_channels)
				{
					throw new Exception("Unknown channel number");
				}

				byte[] cmd_data = {(byte)channel, Convert.ToByte(enable) };
				send_command (cmd_brd_adcfilter, cmd_data, 0, packet_timeout);
			} catch (Exception e) {
				throw new Exception("Failed to set A/D filter flag:" + e.Message);
			}
		}

		public float GetADCValue(uint channel)
		{
			try {
				if (channel >= adc_num_channels)
				{
					throw new Exception("Unknown channel number");
				}
				
				byte[] cmd_data = {(byte)channel};
				byte[] response = send_command (cmd_brd_adc, cmd_data, 4, packet_timeout);
				return BitConverter.ToSingle(response,0);
			} catch (Exception e) {
				throw new Exception("Failed to read A/D value:" + e.Message);
			}
		}

		public float GetThermoValue()
		{
			try {
				byte[] response = send_command (cmd_brd_egt, null, 5, packet_timeout);
				return BitConverter.ToSingle(response,1);
			} catch (Exception e) {
				throw new Exception("Failed to read EGT value:" + e.Message);
			}
		}

		public void CAN_ConnectECU (int _selected_ecu)
		{
			Debug.Assert (_selected_ecu > -1 &&
				_selected_ecu < ECUDescriptors.Count);

			if (selected_ecu == _selected_ecu)
			{
				// already connected
				return;
			}

			try {
				byte[] cmd_data = {1, (byte)_selected_ecu };
				send_command (cmd_can_ecuconnect, cmd_data, 0, 10000);

				// session started; store ECU index
				selected_ecu = _selected_ecu;
			} catch (Exception e) {
				throw new Exception("Failed to connect to ECU:" + e.Message);
			}
		}

		public void CAN_DisconnectECU (bool reset)
		{
			if (selected_ecu < 0) {
				// already disconnected
				return;
			}
			
			try {
				byte[] cmd_data = {0,  Convert.ToByte (reset)};
				send_command (cmd_can_ecuconnect, cmd_data, 0, packet_timeout);
			} catch (Exception e) {
				throw new Exception ("Failed to disconnect from ECU:" + e.Message);
			}

			// cleanup
			finally
			{
				selected_ecu = -1;
			}
		}

		public void CAN_ReadFlash (String _file_name)
		{
			try {
				if (selected_ecu < 0) {
					// session was not started
					throw new Exception ("Not connected to ECU");
				}

				if (OperationRunning ()) {
					throw new Exception ("Operation already in progress");
				}

				// launch operation
				file_name = _file_name;
				begin_operation (new ThreadStart (read_flash_can));
			}

			// handle exception
			catch (Exception e)
			{
				throw new Exception("Failed to read flash:" + e.Message);
			}
		}

		public void CAN_WriteFlash (String _file_name, byte method)
		{
			try {
				if (selected_ecu < 0) {
					// session was not started
					throw new Exception ("Not connected to ECU");
				}
				
				if (OperationRunning ()) {
					throw new Exception ("Operation already in progress");
				}
				
				// launch operation
				file_name = _file_name;
				flash_method = method;
				begin_operation (new ThreadStart (write_flash_can));
			}
			
			// handle exception
			catch (Exception e)
			{
				throw new Exception("Failed to write flash:" + e.Message);
			}
		}

		public void CAN_SetBitrate (uint bitrate)
		{
			try {	
				byte[] cmd_data = put_dword(bitrate);
				send_command (cmd_can_bitrate, cmd_data, 0, packet_timeout);
			} catch (Exception e) {
				throw new Exception("Failed to set CAN bitrate:" + e.Message);
			}
		}

		public void CAN_Open (bool open)
		{
			try {	
				byte[] cmd_data = {Convert.ToByte(open) };
				send_command (cmd_can_open, cmd_data, 0, packet_timeout);
			} catch (Exception e) {
				throw new Exception(open ? "Failed to open CAN channel:" :
				                    "Failed to close CAN channel:" + e.Message);
			}
		}

		public bool CAN_GetMessage (ref caCANFrame frame, uint timeout)
		{
			if (timeout > 0 && !frame_event.WaitOne ((int)timeout)) {
				// timeout expired
				return false;
			}

			frame_lock.WaitOne ();
			bool ret = false;
			if (frame_queue.Count > 0) {
				// get oldest frame
				frame = (caCANFrame)frame_queue.Dequeue();
				ret = true;
			}

			// reset the event if no more messages
			if (frame_queue.Count < 1)
			{
				frame_event.Reset();
			}

			frame_lock.ReleaseMutex();
			return ret;
		}

		public void CAN_SendMessage (ref caCANFrame frame)
		{
			try
			{
				byte[] cmd_data = new byte[15];
				Debug.Assert (cmd_data != null);

				// format command data
				BitConverter.GetBytes(frame.id).CopyTo(cmd_data,0);
				BitConverter.GetBytes(frame.data).CopyTo(cmd_data,4);
				cmd_data[12] = frame.length;
				cmd_data[13] = frame.is_extended;
				cmd_data[14] = frame.is_remote;

				// send message; NB; adapter won't answer!
				send_command (cmd_can_txframe, cmd_data, 0, packet_timeout);
			} 
			// handle exceptions
			catch (Exception e) {
				throw new Exception("Failed to send CAN message:" + e.Message);
			}
		}

		private byte[] put_word (uint a)
		{
			byte[] data = {(byte)(a >> 8),(byte)a};
			return data;
		}

		private byte[] put_dword (uint a)
		{
			byte[] data = {(byte)(a>>24),(byte)(a>>16),(byte)(a>>8),(byte)a};
			return data;
		}

		void send_break(byte cmd_code, uint timeout)
		{
			byte[] cmd = {cmd_code, 0, 0, term_nack};
			write_usb(cmd, (int)timeout);
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

		byte[] send_command (byte cmd_code, byte[] cmd_data, ushort reply_data_len, int timeout)
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
			// wait for response
			if (!response_event.WaitOne(timeout))
			{
				throw new Exception ("Command timed out");
			}
			
			response_lock.WaitOne();
			
			Debug.Assert(response_queue.Count > 0);
			caPacket pack = (caPacket)response_queue.Dequeue();
			
			if (response_queue.Count < 1)
			{
				// put threads on hold until new responses appear
				response_event.Reset();
			}
			
			response_lock.ReleaseMutex();
			
			// check packet contents
			if (pack.cmd_code != cmd_code)
			{
				// response queue messed up due to packet loss or logic fault
				throw new Exception ("Unexpected response");
			}
			
			if (pack.term != term_ack)
			{
				// adapter reported command failure due to parametre error in
				// last command or execution failed
				throw new Exception ("Command failed");
			}
			
			if (pack.data_len != reply_data_len)
			{
				// faulty program logic; packet damage not likely, ince command
				// code and terminator were in place
				throw new Exception("Unexpected response data length");
			}
			
			return (pack.data_len > 0 ? pack.data : null);
		}

		private void clear_responses()
		{
			response_lock.WaitOne();
			
			if (response_queue.Count > 0) {
				// clear queue
				response_queue.Clear();
				// put threads on hold until new responses appear
				response_event.Reset();
			}
			response_lock.ReleaseMutex();
		}

		protected override void process_usb()
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
					Debug.Assert(in_pack.data != null);
					
					for (byte_i = 0; byte_i < in_pack.data_len; ++byte_i)
					{
						in_pack.data[byte_i] = (byte)usb_queue.Dequeue();
					}
				}
				
				// teminator
				in_pack.term = (byte)usb_queue.Dequeue();
				pack_incomplete = false;

				// got CAN frame
				if(in_pack.cmd_code == cmd_can_frame)
				{
					if(in_pack.data_len == 15)
					{
						// fill frame structure
						caCANFrame can_frame = new caCANFrame();
						can_frame.id = (uint)BitConverter.ToInt32(in_pack.data, 0);
						can_frame.data = (ulong)BitConverter.ToInt64(in_pack.data, 4);
						can_frame.length = in_pack.data[12];
						can_frame.is_extended = in_pack.data[13];
						can_frame.is_remote = in_pack.data[14];

						frame_lock.WaitOne();

						// store frame in queue and notify waiting threads
						frame_queue.Enqueue(can_frame);
						frame_event.Set();

						frame_lock.ReleaseMutex();
					}
				}

				// got command response
				else
				{
					response_lock.WaitOne();

					// save response packet and notify waiting threads
					caPacket new_pack = new caPacket();
					new_pack.cmd_code = in_pack.cmd_code;
					new_pack.data = in_pack.data_len != 0 ? (byte[])in_pack.data.Clone() : null;
					new_pack.data_len = in_pack.data_len;
					new_pack.term = in_pack.term;
					response_queue.Enqueue(new_pack);
					response_event.Set();
					
					response_lock.ReleaseMutex();
				}
			}
		}

		private void read_flash_can()
		{
			Debug.Assert(selected_ecu > -1);
			Debug.Assert(!String.IsNullOrEmpty(file_name));

			FileStream fs = new FileStream(file_name,FileMode.Create);
			uint crc = 0;

			try
			{
				Debug.Assert (fs != null);

				// init local checksum
				begin_crc32(ref crc);

				// read flash contents
				uint bytes_read = 0;
				byte[] data_in;

				while(bytes_read < ECUDescriptors[selected_ecu].flash_size) 
				{
					// get data block from flash
					if(bytes_read == 0)
					{
						data_in = send_command(cmd_can_readflash, null, transfer_block_size, packet_timeout);
					}
					else
					{
						data_in = get_response(cmd_can_readflash, transfer_block_size, packet_timeout);
					}
					bytes_read += transfer_block_size;

					// write block to file
					fs.Write(data_in, 0, data_in.Length);
					// update local checksum
					add_crc32(ref crc, data_in);

					// update process
					set_progress(bytes_read);
				}

				// end local checksum
				end_crc32(ref crc);

				// receive remote checksum
				data_in = get_response(cmd_can_readflash, 4, packet_timeout);
				// compare checksum
				if(crc != BitConverter.ToUInt32(data_in,0))
				{
					throw new Exception("Checksums do not match");
				}

				// set result flash
				set_result(true);
			}

			// handle exceptions
			catch(Exception e)
			{
				// interrupt running operation
				send_break(cmd_can_readflash, packet_timeout);
				Thread.Sleep(100);
				clear_responses();

				set_result(false);

				operation_exception = (e.Message.IndexOf('\n') != -1) ? e : new Exception("Failed to read flash:\n" + e.Message);
			}

			// clean up
			finally
			{
				if(fs != null)
				{
					fs.Close();
				}

				// release thread
				end_operation();
			}
		}

		private void write_flash_can()
		{
			Debug.Assert(selected_ecu > -1);
			Debug.Assert(!String.IsNullOrEmpty(file_name));

			// open source file
			FileStream fs = new FileStream(file_name,FileMode.Open);

			try
			{
				Debug.Assert (fs != null);

				// read file into intermediate buffer
				byte[] bin_buf = new byte[ECUDescriptors[selected_ecu].flash_size];
				Debug.Assert (bin_buf != null);
				fs.Read(bin_buf,0,bin_buf.Length);

				// check data signature, prepare for flashing
				switch(this.selected_ecu)
				{
					// Trionic 7
				case 3:
					if(bin_buf[0] != 0xff || bin_buf[1] != 0xff ||
					   bin_buf[2] != 0xef || bin_buf[3] != 0xfc)
					{
						throw new Exception("File is not a Trionic 7 binary!");
					}
					if(this.flash_method == 1)
					{
						// Remove VIN and immo fields
						this.strip_header_t7(ref bin_buf);
					}
					break;

					// TODO
				}

				// init local checksum
				uint crc = 0;
				begin_crc32(ref crc);

				// start writing
				byte[] cmd_data = {flash_method};
				send_command (cmd_can_writeflash, cmd_data, 0, 5000);

				cmd_data = new byte[transfer_block_size];
				Debug.Assert(cmd_data != null);
				uint bytes_written = 0;

				while(bytes_written < ECUDescriptors[selected_ecu].flash_size)
				{
					// send block from file to adapter
					Array.ConstrainedCopy(bin_buf,(int)bytes_written,cmd_data,0,transfer_block_size);
					send_command (cmd_can_writeflash, cmd_data, 0, 
					              bytes_written > 0 ? packet_timeout : 20000);

					bytes_written += transfer_block_size;

					// update local checksum
					add_crc32(ref crc, cmd_data);

					// update progress
					set_progress(bytes_written);
				}

				// end local checksum
				end_crc32(ref crc);

				// receive remote checksum
				cmd_data = get_response(cmd_can_writeflash, 4, packet_timeout);

				// compare checksums
				if(crc != BitConverter.ToUInt32(cmd_data,0))
				{
					throw new Exception("Checksums do not match");
				}
				
				// set result flag
				set_result(true);
			}

			// handle exceptions
			catch(Exception e)
			{
				// interrupt running operation
				send_break(cmd_can_writeflash, packet_timeout);
				Thread.Sleep(100);
				clear_responses();
				
				set_result(false);
				
				operation_exception = (e.Message.IndexOf('\n') != -1) ? e : new Exception("Failed to write flash:\n" + e.Message);
			}
			
			// clean up
			finally
			{
				if(fs != null)
				{
					fs.Close();
				}
				
				// release thread
				end_operation();
			}
		}

		private void strip_header_t7 (ref byte[] bin_buf)
		{
			Debug.Assert (bin_buf != null);
			Debug.Assert (bin_buf.Length == ECUDescriptors[3].flash_size);

			uint addr = 0x7ffff;
			uint field_len;
			uint field_id;

			while (addr > 0x7fd00)
			{
				// field length
				field_len = bin_buf[addr];
				if(field_len == 0x0 || field_len == 0xff)
				{
					break;
				}
				--addr;

				// field ID
				field_id = bin_buf[addr];
				--addr;

				if(field_id == 0x92)
				{
					// remove header
					addr -= field_len;
					while(addr > 0x7fd00)
					{
						bin_buf[addr] = 0xff;
						--addr;
					}

					return;
				}

				addr -= field_len;
			}
		}

		private void begin_crc32(ref uint crc)
		{
			// set initial remainder
			crc = 0xffffffff;
		}

		private void add_crc32(ref uint crc, byte data)
		{
			byte bit;
			crc ^= (uint)((byte)reflect_crc32 (data, 8)) << 24;
			for (bit = 8; bit > 0; --bit)
			{
				// divide
				crc = Convert.ToBoolean(crc & ( 1 << (32 - 1))) ? ((crc << 1) ^ 0x04c11db7) : (crc << 1);
			}
		}

		private void add_crc32(ref uint crc, byte[] data)
		{
			Debug.Assert(data != null);
			for (int i = 0; i < data.Length; ++i)
			{
				add_crc32(ref crc,data[i]);
			}
		}

		private uint reflect_crc32(uint data, byte num_bits)
		{
			uint reflection = 0;
			uint int_data = data;
			for (byte bit = 0; bit < num_bits; ++bit)
			{
				if(Convert.ToBoolean(int_data & 0x01))
				{
					reflection |= (uint)(1 << (( num_bits - 1) - bit));
				}

				int_data >>= 1;
			}
			return reflection;
		}

		private void end_crc32(ref uint crc)
		{
			// final result
			crc = reflect_crc32(crc, 32) ^ 0xffffffff;
		}
	}
}

