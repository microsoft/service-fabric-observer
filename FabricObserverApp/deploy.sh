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
  echo Error: Unknown option passed in $i
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


PACKAGE_DIR=$DIR/pkg/$Configuration/$KIND_DIR/FabricObserverType

if [ ! -d $PACKAGE_DIR ]
then
  echo Error: The following directory does not exist: $PACKAGE_DIR--application-id
  echo Error: Make sure to run ./build.sh
  exit 1
fi

sfctl application upload --path $PACKAGE_DIR --show-progress

if [ `sfctl application list --output json --query "items[?contains(name, 'fabric:/FabricObserver')]" | grep FabricObserver | wc -l` -gt 0 ]
then
  echo 'Removing FabricObserver application'
  sfctl application delete --application-id 'FabricObserver' --force-remove FORCE_REMOVE
fi

if [ `sfctl application type --application-type-name FabricObserverType | grep FabricObserverType | wc -l` -gt 0 ]
then
  echo 'Removing FabricObserverType'
  APP_VERSION=`sfctl application type --application-type-name FabricObserverType --query 'items[0].version' | xargs`
  sfctl application unprovision --application-type-name FabricObserverType --application-type-version $APP_VERSION
fi

echo 'Provisioning FabricObserverType'
sfctl application provision --application-type-build-path FabricObserverType

echo 'Creating fabric:/FabricObserver application'
APP_VERSION=`sfctl application type --application-type-name FabricObserverType --query 'items[0].version' | xargs`

sfctl application create --app-name fabric:/FabricObserver --app-type FabricObserverType --app-version $APP_VERSION
cd $CURRENT_DIR
