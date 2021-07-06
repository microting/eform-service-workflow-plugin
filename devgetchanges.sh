#!/bin/bash

cd ~

if [ -d "Documents/workspace/microting/eform-service-workflow-plugin/ServiceWorkflowPlugin" ]; then
	rm -fR Documents/workspace/microting/eform-service-workflow-plugin/ServiceWorkflowPlugin
fi

cp -av Documents/workspace/microting/eform-debian-service/Plugins/ServiceWorkflowPlugin Documents/workspace/microting/eform-service-workflow-plugin/ServiceWorkflowPlugin
