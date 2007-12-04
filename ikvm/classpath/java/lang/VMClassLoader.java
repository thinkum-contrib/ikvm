/* VMClassLoader.java -- Reference implementation of native interface
   required by ClassLoader
   Copyright (C) 1998, 2001, 2002 Free Software Foundation

This file is part of GNU Classpath.

GNU Classpath is free software; you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation; either version 2, or (at your option)
any later version.

GNU Classpath is distributed in the hope that it will be useful, but
WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
General Public License for more details.

You should have received a copy of the GNU General Public License
along with GNU Classpath; see the file COPYING.  If not, write to the
Free Software Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA
02111-1307 USA.

Linking this library statically or dynamically with other modules is
making a combined work based on this library.  Thus, the terms and
conditions of the GNU General Public License cover the whole
combination.

As a special exception, the copyright holders of this library give you
permission to link this library with independent modules to produce an
executable, regardless of the license terms of these independent
modules, and to copy and distribute the resulting executable under
terms of your choice, provided that you also meet, for each linked
independent module, the terms and conditions of the license of that
module.  An independent module is a module which is not derived from
or based on this library.  If you modify this library, you may extend
this exception to your version of the library, but you are not
obligated to do so.  If you do not wish to do so, delete this
exception statement from your version. */

package java.lang;

import java.security.ProtectionDomain;
import java.net.URL;
import java.net.MalformedURLException;
import java.io.IOException;
import java.util.Enumeration;
import java.util.Map;
import java.util.HashMap;
import java.util.ArrayList;
import java.util.Collection;
import java.util.Hashtable;
import java.util.StringTokenizer;
import java.util.jar.Attributes;
import java.util.jar.Manifest;
import gnu.classpath.SystemProperties;
import gnu.java.lang.InstrumentationImpl;
import cli.System.*;
import cli.System.Reflection.*;

/**
 * java.lang.VMClassLoader is a package-private helper for VMs to implement
 * on behalf of java.lang.ClassLoader.
 *
 * @author John Keiser
 * @author Mark Wielaard <mark@klomp.org>
 * @author Eric Blake <ebb9@email.byu.edu>
 * @author Jeroen Frijters
 */
final class VMClassLoader
{
    //static InstrumentationImpl instrumenter;

    private static native Class defineClassImpl(ClassLoader cl, String name, byte[] data, int offset, int len, ProtectionDomain pd)
        throws ClassNotFoundException;

    // this method is used by java.lang.reflect.Proxy (through reflection)
    static Class defineClass(ClassLoader cl, String name, byte[] data, int offset, int len, ProtectionDomain pd)
    {
        try
        {
            return defineClassImpl(cl, name, data, offset, len, pd);
        }
        catch(ClassNotFoundException x)
        {
            throw new NoClassDefFoundError(x.getMessage());
        }
    }

    static Class defineClassWithTransformers(ClassLoader cl, String name, byte[] data, int offset, int len, ProtectionDomain pd)
    {
        /*
        if(instrumenter != null)
        {
            if(offset != 0 || len != data.length)
            {
                byte[] tmp = new byte[len];
                System.arraycopy(data, offset, tmp, 0, len);
                data = tmp;
                offset = 0;
            }
            data = instrumenter.callTransformers(cl, name, null, pd, data);
            len = data.length;
        }
        */
        try
        {
            return defineClassImpl(cl, name, data, offset, len, pd);
        }
        catch(ClassNotFoundException x)
        {
            throw new NoClassDefFoundError(x.getMessage());
        }
    }

    /**
     * Helper to resolve all references to other classes from this class.
     *
     * @param c the class to resolve
     */
    static void resolveClass(Class c)
    {
    }

    /**
     * Helper to load a class from the bootstrap class loader.
     *
     * @param name the class name to load
     * @param resolve whether to resolve it
     * @return the class, loaded by the bootstrap classloader
     */
    static native Class loadClass(String name, boolean resolve) throws ClassNotFoundException;

    private static cli.System.Reflection.Assembly getBootstrapAssembly()
    {
	return ikvm.runtime.Util.getInstanceTypeFromClass(Object.class).get_Assembly();
    }

    /**
     * Helper to load a resource from the bootstrap class loader.
     *
     * @param name the resource to find
     * @return the URL to the resource
     */
    static URL getResource(String name)
    {
        return ikvm.runtime.AssemblyClassLoader.getResource(null, getBootstrapAssembly(), name);
    }

    /**
     * Helper to get a list of resources from the bootstrap class loader.
     *
     * @param name the resource to find
     * @return an enumeration of resources
     * @throws IOException if one occurs
     */
    static Enumeration getResources(String name) throws IOException
    {
        return ikvm.runtime.AssemblyClassLoader.getResources(null, getBootstrapAssembly(), name);
    }

    /**
     * Helper to get a package from the bootstrap class loader.  The default
     * implementation of returning null may be adequate, or you may decide
     * that this needs some native help.
     *
     * @param name the name to find
     * @return the named package, if it exists
     */
    static Package getPackage(String name)
    {
        getPackagesImpl();
        return (Package)packages.get(name);
    }

    /**
     * Helper to get all packages from the bootstrap class loader.  The default
     * implementation of returning an empty array may be adequate, or you may
     * decide that this needs some native help.
     *
     * @return all named packages, if any exist
     */
    static Package[] getPackages()
    {
        getPackagesImpl();
        Collection coll = packages.values();
        Package[] pkg = new Package[coll.size()];
        coll.toArray(pkg);
        return pkg;
    }

    private static void getPackagesImpl()
    {
        if(packages == null)
        {
            Hashtable h = new Hashtable();
            String[] pkgs = ikvm.runtime.AssemblyClassLoader.GetPackages(null);
            URL sealBase = null;
            try
            {
                sealBase = new URL(cli.System.Reflection.Assembly.GetExecutingAssembly().get_CodeBase());
            }
            catch(MalformedURLException _)
            {
            }
            for(int i = 0; i < pkgs.length; i++)
            {
                h.put(pkgs[i],
                    new Package(pkgs[i],
                    "Java Platform API Specification",             // specTitle
                    "1.4",                                         // specVersion
                    "Sun Microsystems, Inc.",                      // specVendor
                    "GNU Classpath",                               // implTitle
                    gnu.classpath.Configuration.CLASSPATH_VERSION, // implVersion
                    "Free Software Foundation",                    // implVendor
                    sealBase,                                      // sealBase
                    null));                                        // class loader
            }
            packages = h;
        }
    }

    private static Hashtable packages;

    /**
     * Helper for java.lang.Integer, Byte, etc to get the TYPE class
     * at initialization time. The type code is one of the chars that
     * represents the primitive type as in JNI.
     *
     * <ul>
     * <li>'Z' - boolean</li>
     * <li>'B' - byte</li>
     * <li>'C' - char</li>
     * <li>'D' - double</li>
     * <li>'F' - float</li>
     * <li>'I' - int</li>
     * <li>'J' - long</li>
     * <li>'S' - short</li>
     * <li>'V' - void</li>
     * </ul>
     *
     * Note that this is currently a java version that converts the type code
     * to a string and calls the native <code>getPrimitiveClass(String)</code>
     * method for backwards compatibility with VMs that used old versions of
     * GNU Classpath. Please replace this method with a native method
     * <code>final static native Class getPrimitiveClass(char type);</code>
     * if your VM supports it. <strong>The java version of this method and
     * the String version of this method will disappear in a future version
     * of GNU Classpath</strong>.
     *
     * @param type the primitive type
     * @return a "bogus" class representing the primitive type
     */
    static native Class getPrimitiveClass(char type);

    /**
     * The system default for assertion status. This is used for all system
     * classes (those with a null ClassLoader), as well as the initial value for
     * every ClassLoader's default assertion status.
     *
     * @return the system-wide default assertion status
     */
    static boolean defaultAssertionStatus()
    {
	return Boolean.valueOf(SystemProperties.getProperty("ikvm.assert.default", "false")).booleanValue();
    }

    /**
     * The system default for package assertion status. This is used for all
     * ClassLoader's packageAssertionStatus defaults. It must be a map of
     * package names to Boolean.TRUE or Boolean.FALSE, with the unnamed package
     * represented as a null key.
     *
     * @return a (read-only) map for the default packageAssertionStatus
     */
    static Map packageAssertionStatus()
    {
	if(packageAssertionMap == null)
	{
	    HashMap m = new HashMap();
	    String enable = SystemProperties.getProperty("ikvm.assert.enable", null);
	    if(enable != null)
	    {
		StringTokenizer st = new StringTokenizer(enable, ":");
		while(st.hasMoreTokens())
		{
		    m.put(st.nextToken(), Boolean.TRUE);
		}
	    }
	    String disable = SystemProperties.getProperty("ikvm.assert.disable", null);
	    if(disable != null)
	    {
		StringTokenizer st = new StringTokenizer(disable, ":");
		while(st.hasMoreTokens())
		{
		    m.put(st.nextToken(), Boolean.FALSE);
		}
	    }
	    packageAssertionMap = m;
	}
	return packageAssertionMap;
    }
    private static Map packageAssertionMap;

    /**
     * The system default for class assertion status. This is used for all
     * ClassLoader's classAssertionStatus defaults. It must be a map of
     * class names to Boolean.TRUE or Boolean.FALSE
     *
     * @return a (read-only) map for the default classAssertionStatus
     */
    static Map classAssertionStatus()
    {
	// there is no distinction between the package and the class assertion status map
	// (because the command line options don't make the distinction either)
	return packageAssertionStatus();
    }

    static ClassLoader getSystemClassLoader()
    {
        if("".equals(SystemProperties.getProperty("java.class.path")) &&
            "".equals(SystemProperties.getProperty("java.ext.dirs")))
        {
            Assembly entryAssembly = Assembly.GetEntryAssembly();
            if(entryAssembly != null)
            {
                return getAssemblyClassLoader(entryAssembly);
            }
        }
	return ClassLoader.defaultGetSystemClassLoader();
    }
    private static native ClassLoader getAssemblyClassLoader(Assembly asm);

    static native Class findLoadedClass(ClassLoader cl, String name);
}
