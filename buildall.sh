#/bin/bash
rm -rf export
mkdir export
make -j 24
cp build/ch.bin export/ch.bin
cd configapp
bash buildapp.sh
cd ..
cp configapp/bin/Release/net8.0-windows/win-x64/publish/UsbCdcConfigApp.exe export/UsbCdcConfigApp.exe