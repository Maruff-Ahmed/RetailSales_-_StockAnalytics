// RetailSalesStockAnalytics - Modern Enterprise Client Scripts
document.addEventListener('DOMContentLoaded', function () {
    // Auto-dismiss mobile offcanvas drawer on navigation link click
    var mobileSidebar = document.getElementById('mobileSidebar');
    if (mobileSidebar && window.bootstrap) {
        var navLinks = mobileSidebar.querySelectorAll('.sidebar-link');
        navLinks.forEach(function (link) {
            link.addEventListener('click', function () {
                var offcanvasInstance = bootstrap.Offcanvas.getInstance(mobileSidebar);
                if (offcanvasInstance) {
                    offcanvasInstance.hide();
                }
            });
        });
    }
});
