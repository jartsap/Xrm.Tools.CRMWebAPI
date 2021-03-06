
Current Version CDN - https://xrmtoolscode.azureedge.net/CRMWebAPI.js

You can find more query example information [here]( https://github.com/davidyack/Xrm.Tools.CRMWebAPI/wiki/Query-Examples )

# Usage example

```javascript


var apiconfig = { APIUrl: 'https://orgname.crm.dynamics.com/api/data/v8.0/', AccessToken: "<accesstoken>" };

var crmAPI = new CRMWebAPI(apiconfig);

crmAPI
	.Create("accounts", { "name": "test2" })
		.then(
			function(r){
				console.log('Created: ' + r);
				return crmAPI.Update('accounts', r, { "name": "test2updated"});
			}, 
			function(e){
				console.log(e);
			})
		.then(
			function(r){
				console.log('Updated: ' + r.EntityID);
				return crmAPI.Delete('accounts', r.EntityID);
			}, 
			function(e){
				console.log(e);
			})
		.then(
			function(r){
				console.log('Deleted');
			}, function(e){
				console.log(e);
		})
```
Working with Metadata - include CRMWebAPIMetadata.js

   Current Version CDN - https://xrmtoolscode.azureedge.net/CRMWebAPIMetadata.js
   
 Methods
  - GetOptionSetByName 
  - GetOptionSetUserLabels - returns Label/Value array
  - GetEntityDisplayNameList(LCID) - returns object with DisplayName,DisplayCollectionName,LogicalName,LogicalCollectionName for LCID - or pass null for default LCID
  
  #Metadata Usage example
  ```javascript

crmAPI
	.GetOptionSetUserLabels("my_optionsetname")
		.then(
			function(r){
				console.log('Display Labels: ' + r);
				
			}, 
			function(e){
				console.log(e);
			})
		
		
crmAPI.GetEntityDisplayNameList(1033).then(function(result)        
        {
           //do stuff here...
            
        });
		
```
