/*
 * reports.js
 * ---------------------------------------------------------------------------
 * Shared report-screen helpers. Report tables are rendered server-side so they
 * print correctly; this file only wires up client-side filtering/sorting and
 * number formatting for grids that opt in via data attributes.
 */
(function ($) {
    'use strict';

    function formatMoney(value) {
        var n = parseFloat(value) || 0;
        return n.toLocaleString(undefined, { minimumFractionDigits: 4, maximumFractionDigits: 4 });
    }

    $(function () {
        // Any table marked data-report-grid="true" gets client-side sorting and
        // search. Paging is disabled because financial reports are usually
        // printed in full.
        $('table[data-report-grid="true"]').each(function () {
            $(this).DataTable({
                paging: false,
                info: false,
                order: [],
                columnDefs: [
                    { targets: 'money', className: 'text-end', render: formatMoney }
                ]
            });
        });
    });

    // Expose for other scripts / console debugging.
    window.reportHelpers = { formatMoney: formatMoney };
})(jQuery);
