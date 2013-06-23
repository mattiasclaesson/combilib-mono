using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Diagnostics;

namespace combilibmono
{
	public abstract class caAdapterBase
	{
		// dynamic state
		private Thread operation_thread;	// async operation thread object
		private uint operation_progress;	// progress value
		private bool operation_succeded;	// result flag

		// sync primitives
		static Mutex operation_lock = new Mutex();	// oepration mutex

		// dynamic state
		protected int selected_ecu;		// selected ECU index
		protected String file_name;		// file name
		protected Exception operation_exception;	// last operation exception
		
		// Motorola 68K function codes
		public const uint fc_userdata = 1; // User data space;

		public class caECUDesc 
		{
			public caECUDesc(String _name, String _flash_type, uint _flash_addr,
			                 uint _flash_size, uint _sram_addr, uint _sram_size )
			{
				name = _name;
				flash_type = _flash_type;
				flash_addr = _flash_addr;
				flash_size = _flash_size;
				sram_addr = _sram_addr;
				sram_size = _sram_size;
			}

			public String name;
			public String flash_type;
			public uint flash_addr;
			public uint flash_size;
			public uint sram_addr;
			public uint sram_size;
		};

		public static readonly IList<caECUDesc> ECUDescriptors = new ReadOnlyCollection<caECUDesc>
		( new[] {
			new caECUDesc("Trionic 5.2", 				"28f010", 0x060000, 0x020000, 	0x0,			0x8000),
			new caECUDesc("Trionic 5.5, 28F010 chips",	"28f010", 0x040000, 0x040000, 	0x0,			0x8000),
			new caECUDesc("Trionic 5.5, 29F010 chips",	"29f010", 0x040000, 0x040000, 	0x0,			0x8000),
			new caECUDesc("Trionic 7",					"29f400", 0x0, 		0x080000, 	0xf00000,		0xffff),
			new caECUDesc("Trionic 8", 					"29f400", 0x0, 		0x100000, 	0xf00000,		0xffff),
		});

		public caAdapterBase ()
		{
		}

		// USB connection and adapter management
		public abstract void Open();
		public abstract bool IsOpen();
		public abstract void Close();
		public abstract ushort GetFirmwareVersion();
		//public abstract void UpdateFirmware(String filename);

		// async operations
		protected void begin_operation(ThreadStart thread_start)
		{
			operation_lock.WaitOne();

			// clear old flags & state
			operation_progress = 0;
			operation_succeded = false;
			operation_exception = null;

			try {
				// launch worker thread
				Debug.Assert (thread_start != null);
				operation_thread = new Thread (thread_start);
				Debug.Assert (operation_thread != null);

				operation_thread.Start();
			}

			// clean up
			finally
			{
				operation_lock.ReleaseMutex();
			}
		}

		protected void set_progress(uint progress)
		{
			operation_lock.WaitOne ();
			operation_progress = progress;
			operation_lock.ReleaseMutex();
		}

		protected void set_result(bool succeded)
		{
			operation_lock.WaitOne ();
			operation_succeded = succeded;
			operation_lock.ReleaseMutex();
		}

		protected void end_operation()
		{
			if (!OperationRunning())
			{
				// already ended
				return;
			}

			// clear running status
			operation_lock.WaitOne ();
			operation_thread = null;
			operation_lock.ReleaseMutex();
		}

		// Async operations
		public bool OperationRunning() 
		{
			operation_lock.WaitOne ();
			bool ret = (operation_thread != null && operation_thread.IsAlive);
			operation_lock.ReleaseMutex();
			return ret;
		}

		public uint GetOperationProgress()
		{
			operation_lock.WaitOne ();
			uint ret = operation_progress;
			operation_lock.ReleaseMutex();
			return ret;
		}

		public bool OperationSucceeded()
		{
			operation_lock.WaitOne ();
			bool ret = operation_succeded;
			operation_lock.ReleaseMutex();
			return ret;
		}

		public Exception GetOperationException()
		{
			operation_lock.WaitOne ();
			Exception ret = operation_exception;
			operation_lock.ReleaseMutex();
			return ret;
		}
	}
}

