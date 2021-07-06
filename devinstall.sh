#!/bin/bash

cd ~

if [ -d "Documents/workspace/microting/eform-debian-service/Plugins/ServiceWorkflowPlugin" ]; then
	rm -fR Documents/workspace/microting/eform-debian-service/Plugins/ServiceWorkflowPlugin
fi

mkdir Documents/workspace/microting/eform-debian-service/Plugins

cp -av Documents/workspace/microting/eform-service-workflow-plugin/ServiceWorkflowPlugin Documents/workspace/microting/eform-debian-service/Plugins/ServiceWorkflowPlugin
