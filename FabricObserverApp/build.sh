#!/bin/bash

Configuration=Debug
SelfContained=false

for i in "$@"
do
  case $i in
  --Debug|-d)
  Configuration=Debug
  ;;
  --self-contained)
  SelfContained=true
  ;;
  --Release|-r)
  Configuration=Release
  ;;
  *)
  echo Error: Unknown option passed in
  exit 1
  ;;
  esac
done

CURRENT_DIR=`pwd`
DIR=$(cd `dirname $0` && pwd)

if [ $SelfContained == 'true' ]
then
  KIND_DIR=SelfContained
else
  KIND_DIR=FrameworkDependent
fi

APP_TYPE_DIR=$DIR/pkg/$Configuration/$KIND_DIR/FabricObserverType
PACKAGE_DIR=$APP_TYPE_DIR/FabricObserverPkg
CONFIG_DIR=$PACKAGE_DIR/Config
CODE_DIR=$PACKAGE_DIR/Code

rm -rf `dirname $APP_TYPE_DIR`

mkdir -p $CONFIG_DIR

cp $DIR/../FabricObserver/PackageRoot/Config/* $CONFIG_DIR
sed -i 's/C:\\observer_logs/observer_logs/g' $CONFIG_DIR/Settings.xml
sed -i 's/observer_logs\\/observer_logs\//g' $CONFIG_DIR/Settings.xml

cp $DIR/../FabricObserver/PackageRoot/ServiceManifest.xml $PACKAGE_DIR
sed -i 's/FabricObserver.exe/FabricObserver/g' $PACKAGE_DIR/ServiceManifest.xml

cp $DIR/ApplicationPackageRoot/ApplicationManifest.Linux.xml $APP_TYPE_DIR/ApplicationManifest.xml

cd $DIR/../FabricObserver

dotnet build -c $Configuration

dotnet publish -o $CODE_DIR -c $Configuration -r linux-x64 --self-contained $SelfContained

cd $CURRENT_DIR
