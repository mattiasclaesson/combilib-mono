A simple readme for getting things going in Linux:

1. Install mono and libusb.
2. Try to build the project by invoking xbuild:
    xbuild
3. If everything built fine. Try to run it:
    mono ./T7Flash/Source/bin/Debug/T7CANFlasher.exe

Debugging:

Chances are that it does not work correctly. Here is how you debug it:
    mono --trace=System ./T7Flash/Source/bin/Debug/T7CANFlasher.exe > dump
Look for "Exception" in the dump file.

If the program complains about libusb being missing, you might need to fix
your mono config. A link is provided to mono's homepage explaining how you can
fix the problem. In my case, my config was missing a dll entry for libusb. 
Here is how I solved the problem:

Look for libusb in /lib*:

    find /lib* -name "*libusb*"

Add a corresponding entry in /etc/mono/config:

    <dllmap dll="libusb-1.0.dll" target="/lib64/libusb-1.0.so.0" os="!windows"/>

Finally, I had some permission problems. Either fix these udev permissions (you
can search for a howto on the www) or run it as root, if you have sudo
installed you can just prepend sudo to the command described in point 3 above.
