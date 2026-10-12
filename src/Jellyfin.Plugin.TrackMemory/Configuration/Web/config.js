(function () {
    'use strict';

    const pluginId = '5180f998-b8fc-41e0-8c6e-005f4564ec7a';

    function loadConfig(page) {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            page.querySelector('#EnablePlugin').checked = config.EnablePlugin !== false;
            page.querySelector('#EnableAudioMemory').checked = config.EnableAudioMemory !== false;
            page.querySelector('#EnableSubtitleMemory').checked = config.EnableSubtitleMemory !== false;
            page.querySelector('#EnableDebugLogging').checked = config.EnableDebugLogging === true;
            Dashboard.hideLoadingMsg();
        });
    }

    function saveConfig(page, e) {
        e.preventDefault();
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.EnablePlugin = page.querySelector('#EnablePlugin').checked;
            config.EnableAudioMemory = page.querySelector('#EnableAudioMemory').checked;
            config.EnableSubtitleMemory = page.querySelector('#EnableSubtitleMemory').checked;
            config.EnableDebugLogging = page.querySelector('#EnableDebugLogging').checked;

            ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
            });
        });

        return false;
    }

    window.TrackMemoryConfig = {
        onSubmit: saveConfig,
        onPageShow: function () {
            const page = this;
            loadConfig(page);
            page.querySelector('#TrackMemoryConfigForm').addEventListener('submit', function (e) {
                saveConfig(page, e);
            });
        }
    };
})();
