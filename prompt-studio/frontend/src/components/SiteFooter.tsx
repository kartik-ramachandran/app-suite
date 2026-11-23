import { Link } from 'react-router-dom'

interface SiteFooterProps {
  isAuthenticated?: boolean
}

function SiteFooter({ isAuthenticated = false }: SiteFooterProps) {
  return (
    <footer className="site-footer">
      <div className="footer-container">
        <div className="footer-content">
          <div className="footer-section">
            <h4>Product</h4>
            <ul>
              <li><Link to="/library">Public Library</Link></li>
              {isAuthenticated ? (
                <>
                  <li><Link to="/persona-library">Persona Library</Link></li>
                  <li><Link to="/dashboard">Dashboard</Link></li>
                </>
              ) : (
                <>
                  <li><Link to="/library">Public Library</Link></li>
                </>
              )}
            </ul>
          </div>
          <div className="footer-section">
            <h4>Resources</h4>
            <ul>
              <li><Link to="/free-pdf-tools">Free PDF Tools</Link></li>
              <li><Link to="/about">About Us</Link></li>
              <li><Link to="/faq">FAQ</Link></li>
              <li><Link to="/blog">Blog</Link></li>
              <li><Link to="/pricing">Pricing</Link></li>
              <li><Link to="/contact">Contact Us</Link></li>
            </ul>
          </div>
          <div className="footer-section">
            <h4>Legal</h4>
            <ul>
              <li><Link to="/privacy">Privacy Policy</Link></li>
              <li><Link to="/terms">Terms & Conditions</Link></li>
            </ul>
          </div>
        </div>
        <div className="footer-bottom">
          <p>&copy; {new Date().getFullYear()} GenPrompt. All rights reserved.</p>
        </div>
      </div>
    </footer>
  )
}

export default SiteFooter
